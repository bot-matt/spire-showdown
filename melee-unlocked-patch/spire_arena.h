// SPDX-License-Identifier: GPL-2.0-or-later
#pragma once
#include <cstdint>
#include <string>
namespace host { struct PadState; }
namespace spire {
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
