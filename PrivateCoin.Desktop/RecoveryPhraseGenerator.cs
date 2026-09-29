using System;
using System.Security.Cryptography;

namespace PrivateCoin.Desktop
{
    internal static class RecoveryPhraseGenerator
    {
        // A power-of-two word list lets every random byte select a word without modulo bias.
        private static readonly string[] EnglishWords =
        {
            "actor", "advice", "airport", "album", "anchor", "apple", "artist", "autumn",
            "bamboo", "basket", "beach", "beauty", "bicycle", "bird", "border", "breeze",
            "bridge", "brother", "cactus", "camera", "candle", "canyon", "carpet", "castle",
            "celery", "circle", "cloud", "coffee", "comet", "coral", "cotton", "crystal",
            "dance", "desert", "diamond", "doctor", "dolphin", "dragon", "dream", "eagle",
            "earth", "engine", "fabric", "family", "feather", "field", "forest", "friend",
            "galaxy", "garden", "gentle", "giraffe", "globe", "gold", "grape", "green",
            "harbor", "harmony", "hazel", "honey", "island", "jacket", "jungle", "kitten",
            "ladder", "lake", "lemon", "light", "lily", "magic", "maple", "meadow",
            "melon", "meteor", "mirror", "monkey", "moon", "morning", "mountain", "music",
            "nature", "ocean", "olive", "orange", "orchid", "panda", "paper", "peanut",
            "pepper", "piano", "planet", "pocket", "prairie", "purple", "rabbit", "rainbow",
            "river", "rocket", "sailor", "shadow", "silver", "sister", "spring", "star",
            "stone", "summer", "sunset", "table", "tiger", "tomato", "travel", "tulip",
            "turtle", "valley", "velvet", "violet", "water", "whale", "willow", "window",
            "winter", "wonder", "yellow", "zebra", "zero", "zone", "acorn", "button"
        };

        public static string Generate()
        {
            var randomBytes = new byte[12];
            using (RandomNumberGenerator random = RandomNumberGenerator.Create())
                random.GetBytes(randomBytes);

            var words = new string[randomBytes.Length];
            for (int index = 0; index < words.Length; index++)
                words[index] = EnglishWords[randomBytes[index] & (EnglishWords.Length - 1)];
            return string.Join(" ", words);
        }
    }
}
