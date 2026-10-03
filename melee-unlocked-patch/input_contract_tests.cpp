#include "spire_arena.h"
#include <array>
#include <cassert>
using spire::use_forwarded_input;
static_assert(!use_forwarded_input("auto",true,true,false,false),"neutral Godot must not erase native input");
static_assert(use_forwarded_input("auto",true,true,true,false),"active Spire input is forwarded");
static_assert(!use_forwarded_input("auto",true,false,true,false),"stale samples leave native input usable");
static_assert(use_forwarded_input("spire",true,true,false,false),"strict Spire mode releases buttons with a neutral sample");
static_assert(!use_forwarded_input("native",true,true,true,false),"native bindings must remain in control");
static_assert(use_forwarded_input("gamecube_adapter",true,true,false,true),"Linux USB neutral packet releases held buttons");
static_assert(!use_forwarded_input("gamecube_adapter",true,true,true,false),"adapter mode must not accept an unrelated pad");
static_assert(!use_forwarded_input("gamecube_adapter",false,true,true,true),"disconnected adapter is ignored");
int main() {
  std::array<uint8_t,0x60> rules;
  rules.fill(0xA5); rules[0xB]=2; // Retail defaults enable items.
  spire::disable_items(rules.data(),rules.size());
  assert(rules[0xB]==0xFF);
  for(int i=0x20;i<0x28;++i) assert(rules[i]==0);
  assert(rules[0x1F]==0xA5 && rules[0x28]==0xA5); // Do not corrupt adjacent fields.
  std::array<uint8_t,0x27> short_rules{};
  short_rules.fill(0xA5);
  spire::disable_items(short_rules.data(),short_rules.size());
  assert(short_rules[0xB]==0xA5);
}
