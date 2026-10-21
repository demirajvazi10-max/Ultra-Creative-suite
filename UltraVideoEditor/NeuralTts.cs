using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UltraVideoEditor
{
    /// <summary>
    /// v1.4.5 — natural (neural) voices for the audio description.
    ///
    /// Two services:
    ///  • Microsoft neural voices — the voices Microsoft Edge uses for "Read aloud". Free, no key,
    ///    no account (Serbian: Sofija and Nikola). It is not an official developer service, so it
    ///    may change one day; when it fails, the caller falls back to another voice.
    ///  • Google Cloud Chirp 3 HD — Google's newest voices. Needs a Google Cloud API key with the
    ///    Text-to-Speech API turned on (the first million characters every month are free).
    ///
    /// Voice tags used in the voice list: "ms:&lt;voice name&gt;" and "gc:&lt;voice name&gt;".
    /// Serbian is sent in Cyrillic letters: both services read Serbian Cyrillic most reliably.
    /// </summary>
    public static class NeuralTts
    {
        public const string GoogleKeyName = "google_tts";

        public static string GoogleKey => KeyStore.Load(GoogleKeyName);

        private static string Locale(string lang) => lang switch { "sr" => "sr-RS", "de" => "de-DE", _ => "en-US" };

        /// <summary>The Microsoft voices for a language: (tag, female/male description).</summary>
        public static List<(string Tag, string Name)> MicrosoftVoices(string lang) => lang switch
        {
            "sr" => new() { ("ms:sr-RS-SophieNeural", "Sofija"), ("ms:sr-RS-NicholasNeural", "Nikola") },
            "de" => new() { ("ms:de-DE-KatjaNeural", "Katja"), ("ms:de-DE-ConradNeural", "Conrad") },
            _    => new() { ("ms:en-US-JennyNeural", "Jenny"), ("ms:en-US-GuyNeural", "Guy") },
        };

        /// <summary>The Google Chirp 3 HD voices for a language (a female and a male voice).</summary>
        public static List<(string Tag, string Name)> GoogleVoices(string lang)
        {
            string loc = Locale(lang);
            return new() { ($"gc:{loc}-Chirp3-HD-Kore", "Kore"), ($"gc:{loc}-Chirp3-HD-Charon", "Charon") };
        }

        // ── Microsoft (Edge "Read aloud") ──────────────────────────────────────

        private static string Stamp() => DateTime.UtcNow.ToString("ddd MMM dd yyyy HH:mm:ss 'GMT+0000 (Coordinated Universal Time)'", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>Speaks one text with a Microsoft neural voice and returns MP3 bytes.</summary>
        public static async Task<byte[]> MicrosoftAsync(string text, string voice, CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(40));
            var token = timeout.Token;

            ClientWebSocket ws = null;
            for (int attempt = 0; ; attempt++)
            {
                string connId = Guid.NewGuid().ToString("N");
                var uri = new Uri("wss://speech.platform.bing.com/consumer/speech/synthesize/readaloud/edge/v1" +
                                  $"?TrustedClientToken={EdgeTtsToken.TrustedClientToken}&ConnectionId={connId}" +
                                  $"&Sec-MS-GEC={EdgeTtsToken.Generate()}&Sec-MS-GEC-Version={EdgeTtsToken.GecVersion}");
                ws = new ClientWebSocket();
                string major = EdgeTtsToken.ChromiumVersion.Split('.')[0];
                ws.Options.SetRequestHeader("Origin", "chrome-extension://jdiccldimpdaibmpdkjnbmckianbfold");
                ws.Options.SetRequestHeader("User-Agent",
                    $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{major}.0.0.0 Safari/537.36 Edg/{major}.0.0.0");
                ws.Options.SetRequestHeader("Pragma", "no-cache");
                ws.Options.SetRequestHeader("Cache-Control", "no-cache");
                ws.Options.SetRequestHeader("Accept-Language", "en-US,en;q=0.9");
                ws.Options.SetRequestHeader("Cookie", $"muid={Convert.ToHexString(RandomNumberGenerator.GetBytes(16))};");
                ws.Options.CollectHttpResponseDetails = true;
                try
                {
                    await ws.ConnectAsync(uri, token);
                    break;
                }
                catch (WebSocketException) when (attempt == 0 && ws.HttpStatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    // the token depends on the time: when this computer's clock is off, use the server's
                    if (ws.HttpResponseHeaders != null && ws.HttpResponseHeaders.TryGetValue("Date", out var dates) &&
                        DateTimeOffset.TryParse(dates.FirstOrDefault(), System.Globalization.CultureInfo.InvariantCulture,
                                                System.Globalization.DateTimeStyles.AssumeUniversal, out var server))
                        EdgeTtsToken.ClockSkewSeconds += (server.UtcDateTime - DateTime.UtcNow).TotalSeconds;
                    ws.Dispose();
                }
            }
            using var _ws = ws;

            string config =
                $"X-Timestamp:{Stamp()}\r\nContent-Type:application/json; charset=utf-8\r\nPath:speech.config\r\n\r\n" +
                "{\"context\":{\"synthesis\":{\"audio\":{\"metadataoptions\":{\"sentenceBoundaryEnabled\":\"false\",\"wordBoundaryEnabled\":\"false\"}," +
                "\"outputFormat\":\"audio-24khz-48kbitrate-mono-mp3\"}}}}\r\n";
            await SendText(ws, config, token);

            // "sr-RS-SophieNeural" → "Microsoft Server Speech Text to Speech Voice (sr-RS, SophieNeural)", as Edge sends it
            string fullName = voice;
            var vm = System.Text.RegularExpressions.Regex.Match(voice, @"^([a-z]{2,})-([A-Z]{2,})-(.+Neural)$");
            if (vm.Success) fullName = $"Microsoft Server Speech Text to Speech Voice ({vm.Groups[1].Value}-{vm.Groups[2].Value}, {vm.Groups[3].Value})";
            string ssml =
                "<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='en-US'>" +
                $"<voice name='{fullName}'><prosody pitch='+0Hz' rate='+0%' volume='+0%'>{System.Security.SecurityElement.Escape(text)}</prosody></voice></speak>";
            string request =
                $"X-RequestId:{Guid.NewGuid():N}\r\nContent-Type:application/ssml+xml\r\nX-Timestamp:{Stamp()}Z\r\nPath:ssml\r\n\r\n{ssml}";
            await SendText(ws, request, token);

            using var audio = new MemoryStream();
            var buffer = new byte[16384];
            while (ws.State == WebSocketState.Open)
            {
                using var msg = new MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    msg.Write(buffer, 0, r.Count);
                } while (!r.EndOfMessage);
                if (r.MessageType == WebSocketMessageType.Close) break;

                byte[] data = msg.ToArray();
                if (r.MessageType == WebSocketMessageType.Text)
                {
                    if (Encoding.UTF8.GetString(data).Contains("Path:turn.end")) break;
                }
                else if (data.Length > 2)
                {
                    // binary: 2 bytes header length (big-endian), the header, then the audio
                    int headerLen = (data[0] << 8) | data[1];
                    if (2 + headerLen <= data.Length)
                    {
                        string header = Encoding.UTF8.GetString(data, 2, headerLen);
                        if (header.Contains("Path:audio"))
                            audio.Write(data, 2 + headerLen, data.Length - 2 - headerLen);
                    }
                }
            }
            try { if (ws.State == WebSocketState.Open) await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
            if (audio.Length < 500) throw new InvalidOperationException("The Microsoft voice gave no sound.");
            return audio.ToArray();
        }

        private static Task SendText(ClientWebSocket ws, string text, CancellationToken ct) =>
            ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, true, ct);

        // ── Google Cloud Chirp 3 HD ─────────────────────────────────────────────

        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(40) };

        /// <summary>Speaks one text with a Google Cloud voice and returns MP3 bytes.</summary>
        public static async Task<byte[]> GoogleAsync(string text, string voice, string apiKey, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("No Google Cloud key.");
            string locale = voice.Length > 5 ? voice.Substring(0, 5) : "en-US";
            async Task<HttpResponseMessage> Ask(object voiceSpec)
            {
                var body = new
                {
                    input = new { text },
                    voice = voiceSpec,
                    audioConfig = new { audioEncoding = "MP3" }
                };
                var content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");
                return await _http.PostAsync("https://texttospeech.googleapis.com/v1/text:synthesize?key=" + Uri.EscapeDataString(apiKey.Trim()), content, ct);
            }

            var resp = await Ask(new { languageCode = locale, name = voice });
            if (resp.StatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                // that HD voice does not exist for this language: the default voice of the language
                AppLog.Warn($"Google voice '{voice}' was refused; using the default {locale} voice.");
                resp.Dispose();
                resp = await Ask(new { languageCode = locale });
            }
            using (resp)
            {
                string json = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode)
                {
                    string msg = JObject.Parse(json)["error"]?["message"]?.ToString() ?? resp.StatusCode.ToString();
                    throw new InvalidOperationException("Google Cloud: " + msg);
                }
                string b64 = JObject.Parse(json)["audioContent"]?.ToString();
                if (string.IsNullOrEmpty(b64)) throw new InvalidOperationException("Google Cloud gave no sound.");
                return Convert.FromBase64String(b64);
            }
        }

        /// <summary>A quick test of a key: one short word. Returns null when it works, otherwise the reason.</summary>
        public static async Task<string> TestGoogleKeyAsync(string apiKey, string lang, CancellationToken ct)
        {
            try { await GoogleAsync(lang == "sr" ? "Тест." : "Test.", GoogleVoices(lang)[0].Tag.Substring(3), apiKey, ct); return null; }
            catch (Exception ex) { return ex.Message; }
        }

        // ── Serbian Latin → Cyrillic ───────────────────────────────────────────

        private static readonly (string Lat, string Cyr)[] Digraphs = { ("dž", "џ"), ("lj", "љ"), ("nj", "њ") };
        private static readonly Dictionary<char, char> Single = new()
        {
            ['a']='а',['b']='б',['c']='ц',['č']='ч',['ć']='ћ',['d']='д',['đ']='ђ',['e']='е',['f']='ф',['g']='г',['h']='х',
            ['i']='и',['j']='ј',['k']='к',['l']='л',['m']='м',['n']='н',['o']='о',['p']='п',['r']='р',['s']='с',['š']='ш',
            ['t']='т',['u']='у',['v']='в',['z']='з',['ž']='ж'
        };

        /// <summary>Serbian Latin text in Cyrillic letters (for the voices). Words with q, w, x or y (foreign names) stay as they are.</summary>
        public static string SerbianToCyrillic(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var words = System.Text.RegularExpressions.Regex.Split(text, @"(\W+)");
            var sb = new StringBuilder();
            foreach (string w in words)
            {
                if (w.Length == 0 || !char.IsLetter(w[0]) || w.IndexOfAny("qwxyQWXY".ToCharArray()) >= 0) { sb.Append(w); continue; }
                for (int i = 0; i < w.Length; i++)
                {
                    char c = w[i];
                    bool upper = char.IsUpper(c);
                    string two = i + 1 < w.Length ? w.Substring(i, 2).ToLowerInvariant() : null;
                    var dg = two == null ? default : Digraphs.FirstOrDefault(d => d.Lat == two);
                    if (dg.Lat != null)
                    {
                        string cyr = dg.Cyr;
                        sb.Append(upper ? cyr.ToUpperInvariant() : cyr);
                        i++;
                        continue;
                    }
                    if (Single.TryGetValue(char.ToLowerInvariant(c), out char cy))
                        sb.Append(upper ? char.ToUpperInvariant(cy) : cy);
                    else sb.Append(c);
                }
            }
            return sb.ToString();
        }
    }
}
