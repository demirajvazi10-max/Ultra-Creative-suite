#include "WavWriter.h"

#include <algorithm>
#include <cstdint>
#include <fstream>

namespace
{
    void WriteU32(std::ofstream& f, uint32_t v) { f.write(reinterpret_cast<const char*>(&v), sizeof(v)); }
    void WriteU16(std::ofstream& f, uint16_t v) { f.write(reinterpret_cast<const char*>(&v), sizeof(v)); }
}

bool WriteWavPcm16Stereo(const std::string& path,
                          const std::vector<float>& interleavedStereo,
                          int sampleRate,
                          std::string& outError)
{
    if (sampleRate <= 0)
    {
        outError = "Neispravna frekvencija uzorkovanja.";
        return false;
    }

    std::ofstream f(path, std::ios::binary);
    if (!f.is_open())
    {
        outError = "Fajl se ne moze otvoriti za upis (proveri putanju i dozvole).";
        return false;
    }

    constexpr int channels = 2;
    constexpr int bitsPerSample = 16;
    constexpr int bytesPerSample = bitsPerSample / 8;

    const uint32_t dataBytes = static_cast<uint32_t>(interleavedStereo.size()) * bytesPerSample;
    const uint32_t byteRate = static_cast<uint32_t>(sampleRate) * channels * bytesPerSample;
    const uint16_t blockAlign = static_cast<uint16_t>(channels * bytesPerSample);

    f.write("RIFF", 4);
    WriteU32(f, 36 + dataBytes);
    f.write("WAVE", 4);

    f.write("fmt ", 4);
    WriteU32(f, 16); // PCM format-chunk size
    WriteU16(f, 1);  // 1 = PCM, no compression
    WriteU16(f, static_cast<uint16_t>(channels));
    WriteU32(f, static_cast<uint32_t>(sampleRate));
    WriteU32(f, byteRate);
    WriteU16(f, blockAlign);
    WriteU16(f, static_cast<uint16_t>(bitsPerSample));

    f.write("data", 4);
    WriteU32(f, dataBytes);

    for (float sample : interleavedStereo)
    {
        float clamped = std::max(-1.0f, std::min(1.0f, sample));
        int16_t intSample = static_cast<int16_t>(clamped * 32767.0f);
        f.write(reinterpret_cast<const char*>(&intSample), sizeof(intSample));
    }

    if (!f.good())
    {
        outError = "Doslo je do greske pri upisu fajla.";
        return false;
    }
    return true;
}
