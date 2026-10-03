// SPDX-License-Identifier: GPL-2.0-or-later
// Compiled into Static Recomp. No retail memory mutation on the rendering thread.
#include "spire_arena.h"
#include "host.h"
#include "window.h"
#include "audio.h"
#include "ppc.h"
#include "slippi_online.h"
#include "slippi_net.h"
#include "nlohmann/json.hpp"
#include <windows.h>
#include <fstream>
#include <filesystem>
#include <chrono>
#include <thread>
#include <cstring>
#include <array>
#include <stdexcept>
#include <atomic>
#include <algorithm>

namespace spire {
namespace {
using json = nlohmann::json;
json spec, control;
std::string contract, phase;
bool enabled = false, playing = false, resumed = false, completed = false;
std::atomic<bool> lab{true};
uint64_t pad_sequence = 0, pad_received = 0;
std::chrono::steady_clock::time_point last_control;
std::array<int,4> stocks{{-1,-1,-1,-1}};
std::array<int,4> kinds{{-1,-1,-1,-1}};
uint16_t observed_stage = 0;

bool valid_id(const std::string& value) {
  if (value.empty() || value.size() > 64) return false;
  for (char c : value) if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
      (c >= '0' && c <= '9') || c == '-' || c == '_')) return false;
  return true;
}
void status(const char* next, int winner = -1, const std::string& error = {}) {
  phase = next;
  const int local = spec.value("cpu_test", false) ? 0 : slippi::online::local_player_index();
  json out{{"duel_id",spec["duel_id"]},{"phase",phase},{"error",error}};
  out["winner_idx"] = winner < 0 ? json(nullptr) : json(winner);
  out["local_won"] = winner < 0 || local < 0 ? json(nullptr) : json(winner == local);
  const auto path = std::filesystem::u8path(contract + ".status.json");
  const auto temp = std::filesystem::u8path(contract + ".status.tmp");
  { std::ofstream f(temp, std::ios::binary | std::ios::trunc); f << out.dump(); f.flush();
    if (!f) { host::log("spire: cannot write status"); host::request_exit(3); return; } }
  if (!MoveFileExW(temp.c_str(),path.c_str(),MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH))
    host::request_exit(3);
}
void read_control() {
  if (std::chrono::steady_clock::now() - last_control < std::chrono::milliseconds(8)) return;
  last_control = std::chrono::steady_clock::now();
  try {
    std::ifstream f(std::filesystem::u8path(contract + ".control.json"));
    if (!f) return;
    auto candidate = json::parse(f);
    if (candidate.value("duel_id", std::string()) != spec["duel_id"].get<std::string>()) return;
    control = std::move(candidate);
    host::audio_set_volume(std::clamp(control.value("volume_percent", 0), 0, 100));
    // No Godot controller is a normal state (keyboard or native adapter).
    // A null sample must not suppress resume, cancellation, or Lab settings.
    if (control.count("pad") && control["pad"].is_object() &&
        control["pad"].value("sequence",uint64_t(0)) != pad_sequence) {
      pad_sequence=control["pad"].value("sequence",uint64_t(0)); pad_received=GetTickCount64();
    }
    if (control.count("viewport") && control["viewport"].is_object()) {
      const int w=control["viewport"].value("width",0), h=control["viewport"].value("height",0);
      if (w>=320 && h>=240 && w<=8192 && h<=8192) host::window_set_client_size(w,h);
    }
    lab = control.value("lab_view", spec.value("lab_view", true));
    resumed = control.value("resume", false);
  } catch (const json::exception&) { return; /* Keep the last valid mailbox. */ }
  // request_exit throws the host's exit signal: never swallow it as a parse error.
  if (control.value("cancel", false)) { status("cancelled"); host::request_exit(0); }
}
void boot_cpu(ppc::Context& c, uint8_t* m) {
  const uint32_t saved_lr=c.lr;
  c.r[3]=0x0E; // GM_DEBUG_VS, bypass intro/CSS without a frame-timing guess.
  ppc::call(c,m,0x801A42F8u); // gm_ChangeGameModeAfterCurrentScene
  c.lr=saved_lr;
}
void enter_cpu(ppc::Context& c, uint8_t* m) {
  const uint32_t saved_lr = c.lr;
  ppc::call(c,m,0x801A427Cu); // gm_GetGameModeStateEnterData
  const uint32_t start = c.r[3];
  c.r[3] = start; ppc::call(c,m,0x80167A64u); // gm_SetupRulesDefaults
  for (int i = 0; i < 6; ++i) {
    c.r[3] = start + 0x60u + 0x24u*i;
    ppc::call(c,m,0x8016795Cu); // gm_SetupPlayerDefaults
    host::wr8(start + 0x61u + 0x24u*i,3); // Unused slots never spawn.
  }
  host::wr8(start,(host::rd8(start)&0x1Fu)|0x20u); // Stock, not time.
  host::wr8(start+2,host::rd8(start+2)|0x80u);
  host::wr16(start+0x0E,spec["stage"].get<uint16_t>());
  host::wr8(start+0x60,spec["local_character"].get<uint8_t>());
  host::wr8(start+0x61,0); host::wr8(start+0x62,1);
  host::wr8(start+0x84,2); // Fox, external id
  host::wr8(start+0x85,1); host::wr8(start+0x86,1); host::wr8(start+0x93,9);
  c.r[3] = 0; ppc::call(c,m,0x80168FC4u); // gm_LoadAnnouncer
  c.lr = saved_lr;
}
uint16_t be16(const uint8_t* p) { return uint16_t(p[0])<<8 | p[1]; }
}

bool configure(const char* path, std::string& error) {
  try {
    std::ifstream f(std::filesystem::u8path(path));
    if (!f) throw std::runtime_error("Cannot open Spire duel contract");
    spec = json::parse(f);
    if (!valid_id(spec.at("duel_id").get<std::string>())) throw std::runtime_error("Invalid duel id");
    const int stage = spec.at("stage").get<int>();
    if (stage != 2 && stage != 3 && stage != 8 && stage != 28 && stage != 31 && stage != 32)
      throw std::runtime_error("Not a legal tournament stage");
    if (spec.at("stocks").get<int>() != 1 || spec.at("local_character").get<int>() > 25 ||
        spec.at("local_character").get<int>() < 0) throw std::runtime_error("Invalid one-stock rules");
    contract = path; enabled = true; lab = spec.value("lab_view",true);
    status("launching");
    return true;
  } catch (const std::exception& e) { error = e.what(); return false; }
}
bool active() { return enabled; }
bool lab_view() { return enabled && lab.load(); }
bool adapter_only() { return enabled && spec.value("controller_mode",std::string()) == "gamecube_adapter"; }
void install() {
  if (!enabled) return;
  if (spec.value("cpu_test",false)) {
    // Replace the function entry, not 0x801BFA20 (an inline Slippi cave
    // inside bootOnLeave, which translated direct calls never dispatch to).
    if (!ppc::redirect_to_host(0x801BF9A8u,boot_cpu) || !ppc::redirect_to_host(0x801B13B8u,enter_cpu)) {
      status("failed",-1,"CPU entry hook unavailable"); host::request_exit(3);
    }
  } else {
    const auto& participants = spec["participants"];
    if (participants.size() > 2) {
      // The bridge supplies a validated --local-peer topology. Never silently
      // start a two-player lobby for a three/four-player relic dispute.
      if (!slippi::Matchmaking::local_peer.enabled) {
        status("failed",-1,"FFA requires explicit peer endpoints"); host::request_exit(3);
      }
    }
    auto& cfg = slippi::online::config();
    cfg.lobby_code = spec.at("opponent_connect_code").get<std::string>();
    cfg.lobby_character = spec.at("local_character").get<int>();
    status("connecting");
  }
}
void tick() { if (enabled) read_control(); }
void event(const uint8_t* bytes, uint32_t size) {
  if (!enabled || !bytes || !size || completed) return;
  if (bytes[0] == 0x36 && size >= 0xD4) {
    observed_stage = be16(bytes+0x13);
    for (int i=0;i<4;++i) {
      kinds[i] = bytes[0x66+0x24*i] == 3 ? -1 : bytes[0x65+0x24*i];
      stocks[i] = kinds[i] < 0 ? -1 : bytes[0x67+0x24*i];
    }
    int active_count = 0;
    for (int i=0;i<4;++i) if (kinds[i]>=0) { ++active_count;
      if (stocks[i]!=1) { status("failed",-1,"Engine did not apply one-stock rules"); host::request_exit(3); return; }
    }
    const int wanted_count = spec.value("cpu_test",false) ? 2 : int(spec["participants"].size());
    if (observed_stage != spec["stage"].get<uint16_t>() || active_count != wanted_count) {
      status("failed",-1,"Engine started with different stage/player rules"); host::request_exit(3); return;
    }
    if (spec.value("cpu_test",false) && (kinds[0] != spec["local_character"].get<int>() || kinds[1]!=2 || bytes[0x98]!=9)) {
      status("failed",-1,"Engine did not apply requested characters/level-9 Fox"); host::request_exit(3); return;
    }
    status("ready");
    // Hold before READY/GO until Spire finishes the summon and reveals the
    // embedded surface. The game loop in Spire stays asynchronous throughout.
    const auto deadline = std::chrono::steady_clock::now()+std::chrono::seconds(30);
    while (!resumed && std::chrono::steady_clock::now()<deadline) {
      read_control(); host::window_pump(); std::this_thread::sleep_for(std::chrono::milliseconds(4));
    }
    if (!resumed) { status("failed",-1,"Spire did not reveal the arena"); host::request_exit(3); return; }
    playing = true;
  } else if (bytes[0]==0x38 && size>=0x22 && bytes[5]<4 && !bytes[6]) {
    stocks[bytes[5]]=bytes[0x21];
  } else if (bytes[0]==0x39 && playing) {
    int winner=-1, alive=0;
    for (int i=0;i<4;++i) if (kinds[i]>=0 && stocks[i]>0) { ++alive; winner=i; }
    // Placements are preferred when available; older streams can omit them.
    if (size>=7 && bytes[1]==2) {
      winner=-1; alive=0;
      for(int i=0;i<4;++i) if(kinds[i]>=0 && bytes[3+i]==0) { ++alive; winner=i; }
    }
    completed=true;
    if (size>=2 && bytes[1]==2 && alive==1) status("completed",winner);
    else status("cancelled",-1,"No unique winner (disconnect, quit, or tied ending)");
  }
}
void input(host::PadState pads[4]) {
  if (!enabled) return;
  // Each match owns a fresh private memory card. A retail boot can wait for
  // confirmation of its first save; acknowledge only the boot/card scenes,
  // never menus, online character selection, or gameplay.
  const uint8_t major=host::rd8(0x80479D30u);
  if (!playing && (major==0x28 || major==0x29)) {
    pads[0]={}; pads[0].err=0;
    pads[0].button=host::retrace_count()%30<2 ? 0x0100 : 0;
    return;
  }
  if (adapter_only()) return;
  read_control();
  try {
    if (!control.count("pad") || !control["pad"].is_object() ||
        !control["pad"].value("connected",false)) return;
    const auto& p = control["pad"];
    // Only a recent sample can hold buttons. Lost focus/disconnect releases all.
    if (GetTickCount64()-pad_received > 250) { pads[0]={}; pads[0].err=-1; return; }
    pads[0].button=p.at("buttons").get<uint16_t>();
    pads[0].stick_x=p.at("sx").get<int8_t>(); pads[0].stick_y=p.at("sy").get<int8_t>();
    pads[0].sub_x=p.at("cx").get<int8_t>(); pads[0].sub_y=p.at("cy").get<int8_t>();
    pads[0].trig_l=p.at("tl").get<uint8_t>(); pads[0].trig_r=p.at("tr").get<uint8_t>(); pads[0].err=0;
    static bool logged_input=false;
    if (!logged_input && (pads[0].button || pads[0].stick_x || pads[0].stick_y)) {
      logged_input=true;
      host::log("spire: applied player-one input (buttons %u, stick %d,%d)",
                unsigned(pads[0].button),int(pads[0].stick_x),int(pads[0].stick_y));
    }
  } catch (...) { /* Hardware/default bindings remain usable. */ }
}
void rules(uint8_t* block,uint32_t size) {
  if (!enabled || size<0xE8 || spec.value("cpu_test",false)) return;
  const auto& participants=spec["participants"];
  block[0x0]=(block[0x0]&0x1F)|0x20; block[0x8]=0; // Stocks, free-for-all.
  const uint16_t stage=spec["stage"].get<uint16_t>();
  block[0xE]=uint8_t(stage>>8); block[0xF]=uint8_t(stage);
  block[0xB]=0xFF; std::memset(block+0x23,0,8); // No items.
  for(int i=0;i<4;++i) {
    block[0x61+0x24*i]=i<int(participants.size())?0:3;
    block[0x62+0x24*i]=1;
    // In a two-player Slippi Direct lobby player indices are assigned by
    // matchmaking, not Spire NetId order. Preserve negotiated characters.
    if (participants.size()>2 && i<int(participants.size()))
      block[0x60+0x24*i]=participants[i]["character"].get<uint8_t>();
  }
}
}
