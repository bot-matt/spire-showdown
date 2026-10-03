"""Offline installer regression checks; only writes inside TemporaryDirectory."""
import hashlib
import json
import os
import pathlib
import re
import subprocess
import tempfile
import unittest
import zipfile


class InstallerTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="spire-installer-test-")
        self.addCleanup(self.temp.cleanup)
        self.root = pathlib.Path(self.temp.name)
        self.game = self.root / "library/steamapps/common/Slay the Spire 2"
        self.game.mkdir(parents=True)
        (self.game / "SlayTheSpire2").touch()
        base = self.root / "library/steamapps/workshop/content/2868840/3737335127/BaseLib"
        base.mkdir(parents=True)
        (base / "BaseLib.dll").touch()
        self.proton = self.root / "proton"
        self.proton.touch()
        self.iso = self.root / "Melee.iso"
        self.iso.write_bytes(b"GALE01\0\2")
        self.config = self.root / "settings/spire-showdown.json"
        self.config.parent.mkdir()
        self.config.write_text(json.dumps({"connect_code": "TEST#123", "lab_view": False, "custom": 42}))
        self.assets = self.root / "assets"
        self.assets.mkdir()
        with zipfile.ZipFile(self.assets / "spire-showdown-mod-Linux-x86_64.zip", "w") as z:
            z.writestr("SpireShowdown/SpireShowdown.dll", b"new-dll")
            z.writestr("SpireShowdown/SpireShowdown.json", '{"version":"fixture"}')
            z.writestr("SpireShowdown/spire-showdown-bridge", "#!/usr/bin/env bash\nexit 0\n")
        with zipfile.ZipFile(self.assets / "spire-showdown-arena-Windows-x86_64.zip", "w") as z:
            z.writestr("arena/melee_port.exe", b"fixture-engine")
            z.writestr("arena/SpireArena.json", '{"protocol":1,"engine":"static_recomp"}')
            z.writestr("arena/Lab/", b"")
            z.writestr("arena/Sys/GameFiles/GALE01/", b"")
        self.sums = self.assets / "SHA256SUMS"
        self.sums.write_text("".join(f"{hashlib.sha256(p.read_bytes()).hexdigest()}  {p.name}\n"
                                    for p in self.assets.glob("*.zip")))
        self.bin = self.root / "bin"
        self.bin.mkdir()
        (self.bin / "pgrep").write_text("#!/bin/sh\nexit 1\n")
        (self.bin / "curl").write_text("""#!/usr/bin/env python3
import os,pathlib,shutil,sys
args=sys.argv[1:]
dest=args[args.index('-o')+1]
url=next(a for a in args if a.startswith('https://'))
shutil.copyfile(pathlib.Path(os.environ['SPIRE_INSTALL_TEST_ASSETS'])/url.rsplit('/',1)[1],dest)
""")
        for p in self.bin.iterdir(): p.chmod(0o755)
        self.install = self.game / "mods/SpireShowdown"
        self.install.mkdir(parents=True)
        (self.install / "SpireShowdown.dll").write_bytes(b"old-dll")
        (self.install / "unrelated.txt").write_text("keep")

    def run_installer(self):
        env = dict(os.environ, PATH=f"{self.bin}:{os.environ['PATH']}",
                   SPIRE_SHOWDOWN_CONFIG=str(self.config), SPIRE_INSTALL_TEST_ASSETS=str(self.assets))
        return subprocess.run(["bash", str(pathlib.Path(__file__).with_name("install-arena-bazzite.sh")),
                               "--game-dir", str(self.game), "--proton", str(self.proton),
                               "--iso", str(self.iso), "--release", "fixture"], env=env,
                              capture_output=True, text=True, timeout=30)

    def test_update_preserves_settings_and_backup(self):
        result = self.run_installer()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual((self.install / "SpireShowdown.dll").read_bytes(), b"new-dll")
        data = json.loads(self.config.read_text())
        self.assertEqual(data["arena_backend"], "melee_unlocked")
        runtime = pathlib.Path(__file__).parents[1] / "mod/SpireShowdown/ArenaRuntime.cs"
        accepted_backend = re.search(r'ArenaBackend\s*==\s*"([^"]+)"', runtime.read_text()).group(1)
        self.assertEqual(data["arena_backend"], accepted_backend)
        self.assertEqual(data["connect_code"], "TEST#123")
        self.assertFalse(data["lab_view"])
        self.assertEqual(data["custom"], 42)
        self.assertTrue((self.install / "unrelated.txt").exists())
        backups = list((self.install / "backups").glob("*/SpireShowdown.dll"))
        self.assertEqual(backups[0].read_bytes(), b"old-dll")
        # Run again to exercise replacement of an already-installed arena.
        result = self.run_installer()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertTrue(list((self.install / "backups").glob("*/arena-replaced/melee_port.exe")))

    def test_checksum_failure_does_not_replace_install(self):
        self.sums.write_text("0" * 64 + "  spire-showdown-mod-Linux-x86_64.zip\n")
        result = self.run_installer()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("Checksum mismatch", result.stderr)
        self.assertEqual((self.install / "SpireShowdown.dll").read_bytes(), b"old-dll")
        self.assertNotIn("arena_backend", json.loads(self.config.read_text()))

    def test_all_installers_use_runtime_backend_and_aliases_match(self):
        folder = pathlib.Path(__file__).parent
        runtime = folder.parent / "mod/SpireShowdown/ArenaRuntime.cs"
        accepted = re.search(r'ArenaBackend\s*==\s*"([^"]+)"', runtime.read_text()).group(1)
        for default, alias in [("install-bazzite.sh", "install-arena-bazzite.sh"),
                               ("install-windows.ps1", "install-arena-windows.ps1")]:
            text = (folder / default).read_text()
            self.assertEqual(text, (folder / alias).read_text())
            self.assertIn("arena_backend='" + accepted + "'", text)

    def test_bad_existing_settings_stop_before_replacement(self):
        self.config.write_text("not-json")
        self.assertNotEqual(self.run_installer().returncode, 0)
        self.assertEqual((self.install / "SpireShowdown.dll").read_bytes(), b"old-dll")
        self.assertEqual(self.config.read_text(), "not-json")


if __name__ == "__main__":
    unittest.main()
