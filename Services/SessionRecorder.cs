using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Warden
{
    /// <summary>
    /// Telemetri değerlerini CSV dosyasına yazar (%AppData%\Warden\sessions).
    /// Excel'in doğrudan açabilmesi için ayırıcı ve ondalık işareti sistemin bölge ayarından alınır
    /// (Türkçe Windows'ta ';' ve ','), dosya UTF-8 BOM ile yazılır.
    /// </summary>
    public sealed class SessionRecorder : IDisposable
    {
        private const long MaxFileBytes = 50L * 1024 * 1024;   // ~günlerce kayıt; disk dolmasın

        private static readonly string[] CategoryOrder = { "CPU", "GPU", "Fans", "Motherboard", "Memory" };

        private readonly object _lock = new();
        private readonly string _folder;
        private readonly CultureInfo _culture;
        private StreamWriter? _writer;
        private List<string>? _columnIds;
        private int _rowsSinceFlush;

        public SessionRecorder(string folder, CultureInfo? culture = null)
        {
            _folder = folder;
            _culture = culture ?? CultureInfo.CurrentCulture;
        }

        public string Folder => _folder;
        public bool IsRecording { get { lock (_lock) return _writer != null; } }
        public bool IsAutomatic { get; private set; }
        public string? Label { get; private set; }
        public string? CurrentFile { get; private set; }

        public event Action? StateChanged;

        public string Start(string label, bool automatic)
        {
            lock (_lock)
            {
                StopInternal();
                Directory.CreateDirectory(_folder);

                string safeLabel = SanitizeFileName(label);
                string path = Path.Combine(_folder, $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{safeLabel}.csv");
                var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                _columnIds = null;
                _rowsSinceFlush = 0;
                IsAutomatic = automatic;
                Label = label;
                CurrentFile = path;
            }
            StateChanged?.Invoke();
            return CurrentFile!;
        }

        public void Stop()
        {
            bool wasRecording;
            lock (_lock)
            {
                wasRecording = _writer != null;
                StopInternal();
            }
            if (wasRecording) StateChanged?.Invoke();
        }

        private void StopInternal()
        {
            try { _writer?.Flush(); _writer?.Dispose(); } catch { }
            _writer = null;
            _columnIds = null;
            IsAutomatic = false;
            Label = null;
        }

        /// <summary>Bir telemetri anlık görüntüsünü satır olarak ekler. Sütunlar ilk satırda sabitlenir.</summary>
        public void Write(TelemetrySnapshot snapshot)
        {
            bool limitReached = false;
            lock (_lock)
            {
                if (_writer == null || snapshot.AllSensors.Count == 0) return;

                string sep = Separator(_culture);
                if (_columnIds == null)
                {
                    var ordered = snapshot.AllSensors
                        .OrderBy(s => Array.IndexOf(CategoryOrder, s.Category) is int i && i >= 0 ? i : CategoryOrder.Length)
                        .ThenBy(s => s.Name, StringComparer.CurrentCulture)
                        .ToList();
                    _columnIds = ordered.Select(s => s.Id).ToList();

                    var header = new List<string> { TimeHeader() };
                    header.AddRange(ordered.Select(s => $"{s.Category} - {s.Name}{(string.IsNullOrEmpty(s.Unit) ? "" : $" ({s.Unit})")}"));
                    _writer.WriteLine(string.Join(sep, header.Select(h => Escape(h, sep))));
                }

                var byId = snapshot.AllSensors.ToDictionary(s => s.Id, s => s.Value, StringComparer.OrdinalIgnoreCase);
                var row = new List<string> { snapshot.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) };
                foreach (var id in _columnIds)
                    row.Add(byId.TryGetValue(id, out float v) && !float.IsNaN(v) ? v.ToString("0.##", _culture) : "");
                _writer.WriteLine(string.Join(sep, row.Select(r => Escape(r, sep))));

                if (++_rowsSinceFlush >= 5)
                {
                    _writer.Flush();
                    _rowsSinceFlush = 0;
                    limitReached = _writer.BaseStream.Length >= MaxFileBytes;
                }
            }
            if (limitReached) Stop();
        }

        private string TimeHeader() => _culture.TwoLetterISOLanguageName == "tr" ? "Zaman" : "Time";

        /// <summary>Bölge ayarının liste ayırıcısı; ondalık işaretiyle çakışırsa ';' kullanılır.</summary>
        public static string Separator(CultureInfo culture)
        {
            string sep = culture.TextInfo.ListSeparator;
            if (string.IsNullOrEmpty(sep) || sep == culture.NumberFormat.NumberDecimalSeparator)
                sep = ";";
            return sep;
        }

        public static string Escape(string field, string separator)
        {
            if (field.Contains(separator) || field.Contains('"') || field.Contains('\n') || field.Contains('\r'))
                return "\"" + field.Replace("\"", "\"\"") + "\"";
            return field;
        }

        public static string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':' }).ToHashSet();
            string cleaned = new string((name ?? "").Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim(' ', '.');
            if (cleaned.Length > 60) cleaned = cleaned[..60];
            return string.IsNullOrEmpty(cleaned) ? "session" : cleaned;
        }

        public void Dispose() => Stop();
    }
}
