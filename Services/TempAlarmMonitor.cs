using System;
using System.Collections.Generic;

namespace Warden
{
    /// <summary>
    /// CPU/GPU sıcaklık alarmı. Sınır anlık sıçramalarda değil, <see cref="Sustain"/> boyunca kesintisiz
    /// aşıldığında tetiklenir; aynı kaynak için <see cref="Cooldown"/> dolmadan tekrar bildirim yapılmaz.
    /// Saf mantık: zaman dışarıdan verilir, test edilebilir.
    /// </summary>
    public sealed class TempAlarmMonitor
    {
        public sealed record Alert(string Source, float Temperature, float Limit);

        public TimeSpan Sustain { get; }
        public TimeSpan Cooldown { get; }

        private sealed class State
        {
            public DateTime? AboveSince;
            public DateTime LastFired = DateTime.MinValue;
        }

        private readonly Dictionary<string, State> _states = new(StringComparer.Ordinal);

        public TempAlarmMonitor(TimeSpan sustain, TimeSpan cooldown)
        {
            Sustain = sustain;
            Cooldown = cooldown;
        }

        /// <summary>Verilen ölçümleri değerlendirir; tetiklenen alarmları döndürür (çoğu zaman boş).</summary>
        public List<Alert> Evaluate(DateTime nowUtc, params (string Source, float? Temperature, float Limit)[] readings)
        {
            var alerts = new List<Alert>();
            foreach (var (source, temperature, limit) in readings)
            {
                if (!_states.TryGetValue(source, out var state))
                    _states[source] = state = new State();

                // Okuma yoksa (sensör yok / PawnIO yok) veya sınırın altındaysa süre sayacı sıfırlanır
                if (temperature is not float t || float.IsNaN(t) || t < limit)
                {
                    state.AboveSince = null;
                    continue;
                }

                state.AboveSince ??= nowUtc;
                if (nowUtc - state.AboveSince.Value >= Sustain && nowUtc - state.LastFired >= Cooldown)
                {
                    state.LastFired = nowUtc;
                    alerts.Add(new Alert(source, t, limit));
                }
            }
            return alerts;
        }

        public void Reset() => _states.Clear();
    }
}
