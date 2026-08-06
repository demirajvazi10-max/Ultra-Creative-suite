using System.Collections.Generic;
using NAudio.Wave;

namespace UltraRecord.Services
{
    public class DeviceOption
    {
        public int Index { get; set; }
        public string Name { get; set; } = "";
    }

    public static class AudioDeviceService
    {
        public static List<DeviceOption> GetInputDevices()
        {
            var list = new List<DeviceOption>();
            for (int i = 0; i < WaveInEvent.DeviceCount; i++)
            {
                var caps = WaveInEvent.GetCapabilities(i);
                list.Add(new DeviceOption { Index = i, Name = caps.ProductName });
            }
            return list;
        }
    }
}
