namespace Warden
{
    public static class ExeNameHelper
    {
        /// <summary>"Oyun Adı (oyun.exe)" biçimindeki görünen metinden exe adını çıkarır.</summary>
        public static string ExtractExeName(string? text)
        {
            string key = (text ?? "").Trim();
            if (key.EndsWith(')') && key.Contains('('))
            {
                int start = key.LastIndexOf('(');
                key = key.Substring(start + 1, key.Length - start - 2).Trim();
            }
            return key;
        }
    }
}
