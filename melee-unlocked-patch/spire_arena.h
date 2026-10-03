// SPDX-License-Identifier: GPL-2.0-or-later
#pragma once
#include <cstdint>
#include <string>
#include <string_view>
namespace host { struct PadState; }
namespace spire {
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
