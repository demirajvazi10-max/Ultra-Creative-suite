#pragma once

#include <string>
#include <vector>

// Writes a plain 16-bit PCM stereo .wav file - the "load this anywhere"
// format, chosen specifically so a rendered composition can be dropped
// straight into Ultra Audio Editor (or any other audio tool) for further
// mixing with vocals, with no format-conversion step in between and no
// lossy compression before that further editing happens.
//
// 'interleavedStereo' holds left/right samples alternating (L0, R0, L1, R1,
// ...), in the usual [-1, 1] float range - values outside that range are
// clamped, not wrapped, matching how the rest of this engine treats
// out-of-range audio (see AudioEngine.cpp's soft-clip comment). Returns
// false and fills 'outError' with a human-readable reason if the file can't
// be written.
bool WriteWavPcm16Stereo(const std::string& path,
                          const std::vector<float>& interleavedStereo,
                          int sampleRate,
                          std::string& outError);
