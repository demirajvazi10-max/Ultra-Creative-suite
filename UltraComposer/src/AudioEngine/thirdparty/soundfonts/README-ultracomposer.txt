Why there's a 30MB .sf2 file sitting in this git repo
======================================================

GeneralUser-GS.sf2 is the default SoundFont bank UltraComposer ships with,
so instrument banks (piano, strings, brass, drums, etc. - like Cakewalk's
built-in TTS-1 sound bank) work the moment you build the app, with no
extra download or setup step.

It is "GeneralUser GS" by S. Christian Collins (v2.0.3), fetched from
https://github.com/mrbumpy409/GeneralUser-GS - see LICENSE.txt in this
folder for the exact terms. Summary: free to use and bundle in your own
software, private or commercial, no attribution required, no copyleft
license attached to it - which is why this one was picked over some other
well-known free GM SoundFonts (e.g. TimGM6mb) that are GPL-2-licensed:
GeneralUser GS's terms sit cleanly alongside this project's own GPL-3.0
license with no license-compatibility question to even ask.

You are not limited to this one bank - SoundFontHost (see
../../SoundFontHost.h/.cpp) can load any .sf2 file you point it at, the
same way VstHost loads any .vst3 file. Bigger/better free SoundFonts
exist (some are hundreds of MB) if you want higher-quality samples later;
this one was chosen specifically for its size (~30MB) so it could be
vendored directly in the repo without needing Git LFS.
