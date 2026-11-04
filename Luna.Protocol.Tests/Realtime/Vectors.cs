namespace Luna.Protocol.Tests.Realtime;

internal static class Vectors
{
    public const string CipherKey = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";

    public const string Plain = "{\"data\":{\"msgs\":[{\"msgId\":\"1\",\"content\":\"xin chào\"}]}}";

    public const string Encrypt3 =
        "ZGVmZ2hpamtsbW5vcHFyc8jJysvMzc7P0NHS09TV1tejkScwkHUF4snbwPogpM5fAQp6re+IqW/nujZfu9slqxSv+qkOv8H7I3cmazcSpPF5MmaK/Jw5dnUHojgtHUwcrX21t/lXEg==";

    public const string Encrypt3UrlEncoded =
        "ZGVmZ2hpamtsbW5vcHFyc8jJysvMzc7P0NHS09TV1tejkScwkHUF4snbwPogpM5fAQp6re%2BIqW%2FnujZfu9slqxSv%2BqkOv8H7I3cmazcSpPF5MmaK%2FJw5dnUHojgtHUwcrX21t%2FlXEg%3D%3D";

    public const string Encrypt2Gzip =
        "ZGVmZ2hpamtsbW5vcHFyc8jJysvMzc7P0NHS09TV1tfHOEtR5BQn2LL6Bt8NnsAsDsPyllbCriSTMp5oqstUXNdVglkUX3Ey8kUd9l3lYKbzb7fjM9JKzOhDdJId4oYsVYw4z07k3EI+1em+F9sAe7908PlwywfC";

    public const string Encrypt2Zlib =
        "ZGVmZ2hpamtsbW5vcHFyc8jJysvMzc7P0NHS09TV1tegL+gHrl0LkeZLB9+N+qJKDMPSxq/Ts59pFBj77WOVO4iL3D8qX09VKz28EYVCwh0RCu56DExB49W7zNLg6MIgjX1OW7BdWSEYyHID";

    public const string Encrypt1Zlib =
        "eJyrVkpJLElUsqpWyi1OL1ayigYzPFOUrJQMlXSUkvPzSlLzSpSslCoy8xSSMw4vyFeqja2tBQD1rxLN";

    public const string Encrypt1Gzip =
        "H4sIAAAAAAAAA6tWSkksSVSyqlbKLU4vVrKKBjM8U5SslAyVdJSS8/NKUvNKlKyUKjLzFJIzDi/IV6qNra0FACCVn0E3AAAA";

    public const string Encrypt1RawDeflate =
        "q1ZKSSxJVLKqVsotTi9WsooGMzxTlKyUDJV0lJLz80pS80qUrJQqMvMUkjMOL8hXqo2trQUA";
}