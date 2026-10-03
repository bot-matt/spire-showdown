// SPDX-License-Identifier: GPL-2.0-or-later
#pragma once
#include <cstdint>
#include <string>
#include <string_view>
#include <cstddef>
namespace host { struct PadState; }
namespace spire {
inline void disable_items(uint8_t* rules,std::size_t size) {
  if(size<0x28) return;
  rules[0x0B]=0xFF; // StartMeleeRules.item_freq: signed -1 means None.
  for(std::size_t i=0x20;i<0x28;++i) rules[i]=0; // Entire big-endian u64 item mask.
}
constexpr bool use_forwarded_input(std::string_view mode,bool connected,bool fresh,bool active,bool linux_adapter) {
  return connected && fresh && (mode=="spire" || (mode=="auto" && active) ||
      (mode=="gamecube_adapter" && linux_adapter));
}
bool configure(const char* path, std::string& error);
bool active();
bool lab_view();
bool adapter_only();
void install();
void tick();
void event(const uint8_t* bytes, uint32_t size);
void input(host::PadState pads[4]);
void rules(uint8_t* block, uint32_t size);
}
