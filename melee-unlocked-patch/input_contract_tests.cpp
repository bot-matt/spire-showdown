#include "spire_arena.h"
using spire::use_forwarded_input;
static_assert(!use_forwarded_input("auto",true,true,false,false),"neutral Godot must not erase native input");
static_assert(use_forwarded_input("auto",true,true,true,false),"active Spire input is forwarded");
static_assert(!use_forwarded_input("auto",true,false,true,false),"stale samples leave native input usable");
static_assert(use_forwarded_input("spire",true,true,false,false),"strict Spire mode releases buttons with a neutral sample");
static_assert(!use_forwarded_input("native",true,true,true,false),"native bindings must remain in control");
static_assert(use_forwarded_input("gamecube_adapter",true,true,false,true),"Linux USB neutral packet releases held buttons");
static_assert(!use_forwarded_input("gamecube_adapter",true,true,true,false),"adapter mode must not accept an unrelated pad");
static_assert(!use_forwarded_input("gamecube_adapter",false,true,true,true),"disconnected adapter is ignored");
int main() {}
