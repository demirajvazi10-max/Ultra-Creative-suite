Why this is a plain source drop instead of a vcpkg package
============================================================

vcpkg does have a "vst3sdk" port, but its manifest explicitly excludes
static-CRT triplets:

    "supports": "!android & !uwp & !(arm64 & windows) & !staticcrt"

UltraComposer.AudioEngine uses the x64-windows-static triplet on purpose
(see the comment in UltraComposer.AudioEngine.vcxproj) so PortAudio and
RtMidi link straight into the DLL instead of needing their own runtime
DLLs next to it. Those two things can't be combined, so instead of
fighting vcpkg, this folder is a small, deliberately trimmed copy of the
official Steinberg VST3 SDK source (https://github.com/steinbergmedia/vst3sdk
and its "pluginterfaces"/"public.sdk" submodules), compiled directly into
this project with the same compiler settings (including the static CRT)
as everything else here. This is also exactly how Steinberg's own example
hosts (public.sdk/samples/vst-hosting/*) are built - linking the hosting
helper sources straight into the host application is the normal way to
use this part of the SDK, not a workaround.

What's in here
---------------
- pluginterfaces/ - the full VST3 interface headers (interfaces only, a
  few small .cpp files for IID plumbing and string helpers). Only
  funknown.cpp from this folder is actually compiled (see
  UltraComposer.AudioEngine.vcxproj) - the SDK doesn't need
  coreiids.cpp/ustring.cpp/conststringtable.cpp for hosting an instrument,
  so they're left un-compiled to avoid a couple of duplicate-symbol
  clashes with InterfaceIds.cpp (see below). They're kept on disk anyway
  so this stays a faithful, easy-to-update copy of the upstream folder.
- public.sdk/source/vst/hosting/ - Steinberg's own helper classes for
  writing a VST3 host: loading a .vst3 module (module.cpp/module_win32.cpp),
  creating and connecting a plug-in's component/controller (plugprovider.cpp),
  and the small IEventList/IParameterChanges/ProcessData implementations a
  host needs to call IAudioProcessor::process(). VstHost.cpp (one level up,
  in src/AudioEngine/) is UltraComposer's own code built on top of these.
- public.sdk/source/vst/utility/ and public.sdk/source/common/ - small
  supporting pieces the files above need (string conversion, thread-safety
  assertions).

../InterfaceIds.cpp (in src/AudioEngine/, not in this folder) is the one
place that actually defines the storage for every interface ID used here
(IComponent::iid and friends) - see the comment at the top of that file
and of pluginterfaces/base/funknown.h (search for INIT_CLASS_IID).

Licensing
---------
Steinberg dual-licenses the VST3 SDK: proprietary, or GPL-3.0. This
project (like the rest of the Ultra Creative Suite) uses the GPL-3.0
track - see LICENSE.txt in this folder for the exact terms that apply to
this SDK copy specifically.

Updating
--------
To pull a newer SDK version, re-copy pluginterfaces/, and just the
public.sdk/source/vst/hosting, public.sdk/source/vst/utility and
public.sdk/source/common subfolders, from a fresh checkout of
https://github.com/steinbergmedia/vst3sdk (remember it uses git
submodules for pluginterfaces/base/public.sdk - "git submodule update
--init" after cloning, or clone each submodule repo directly).
