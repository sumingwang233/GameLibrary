using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GameLibrary.Host.Hosting;

public sealed record TitleTranslationResult(string? Title, string? Provider, string? Error);

/// <summary>Anonymous public translation endpoints. Only the title leaves the process.</summary>
public sealed class TitleTranslationClient(HttpClient http, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim[] _slots = [new(1, 1), new(1, 1)];
    private readonly DateTimeOffset[] _nextStart = new DateTimeOffset[2];
    private readonly DateTimeOffset[] _unavailableUntil = new DateTimeOffset[2];
    private readonly object _sync = new();
    private int _next;

    public bool Unavailable
    {
        get { lock (_sync) return _unavailableUntil.All(time => time > _clock.GetUtcNow()); }
    }

    public async Task<TitleTranslationResult> TranslateAsync(string title, string preference, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length > 1000)
            return new(null, null, "invalidTitle");
        int first;
        lock (_sync)
        {
            first = preference switch { "google" => 0, "bing" => 1, _ => _next };
            if (preference == "balanced") _next = 1 - _next;
        }
        var error = "enginesUnavailable";
        string? lastProvider = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var provider = (first + attempt) % 2;
            await _slots[provider].WaitAsync(token);
            try
            {
                lock (_sync) { if (_unavailableUntil[provider] > _clock.GetUtcNow()) continue; }
                var delay = _nextStart[provider] - _clock.GetUtcNow();
                if (delay > TimeSpan.Zero) await Task.Delay(delay, _clock, token);
                token.ThrowIfCancellationRequested();
                lastProvider = provider == 0 ? "google" : "bing";
                _nextStart[provider] = _clock.GetUtcNow().AddSeconds(1);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12), _clock);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
                using var request = BuildRequest(provider, title);
                try
                {
                    using var response = await http.SendAsync(request, linked.Token);
                    if (!response.IsSuccessStatusCode)
                    {
                        error = response.StatusCode == HttpStatusCode.TooManyRequests ? "rateLimited" : "serviceError";
                        var retry = response.Headers.RetryAfter;
                        var cooldown = response.StatusCode == HttpStatusCode.TooManyRequests
                            ? retry?.Delta ?? (retry?.Date - _clock.GetUtcNow()) ?? TimeSpan.FromSeconds(60)
                            : TimeSpan.FromSeconds(60);
                        Cooldown(provider, cooldown > TimeSpan.Zero ? cooldown : TimeSpan.FromSeconds(60));
                        continue;
                    }
                    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(linked.Token));
                    var translated = ParseResponse(json.RootElement, provider).Trim();
                    if (translated.Length is < 1 or > 4000) throw new JsonException("Invalid translated title");
                    if (title.Contains('-') && translated.Contains("——", StringComparison.Ordinal))
                    {
                        translated = string.Join('：', Regex.Split(translated, @"\s*—{2,}\s*").Where(part => part.Length > 0));
                        if (translated.Length == 0) throw new JsonException("Invalid translated title");
                    }
                    return new(translated, provider == 0 ? "google" : "bing", null);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    error = "timeout";
                    Cooldown(provider, TimeSpan.FromSeconds(60));
                }
                catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException)
                {
                    error = ex is HttpRequestException ? "networkError" : "invalidResponse";
                    Cooldown(provider, TimeSpan.FromSeconds(60));
                }
            }
            finally { _slots[provider].Release(); }
        }
        return new(null, lastProvider, error);
    }

    private void Cooldown(int provider, TimeSpan duration)
    {
        lock (_sync) _unavailableUntil[provider] = _clock.GetUtcNow().Add(duration);
    }

    private static HttpRequestMessage BuildRequest(int provider, string title)
    {
        if (provider == 0)
            return new(HttpMethod.Get, "https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl=zh-CN&dt=t&q=" + Uri.EscapeDataString(title));
        var request = new HttpRequestMessage(HttpMethod.Post,
            "https://edge.microsoft.com/translate/translatetext?to=zh-Hans&isEnterpriseClient=false")
        {
            Content = JsonContent.Create(new[] { title }),
        };
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/113.0.0.0 Safari/537.36 Edg/113.0.1774.42");
        return request;
    }

    private static string ParseResponse(JsonElement root, int provider)
    {
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0) throw new JsonException();
        if (provider == 0)
        {
            if (root[0].ValueKind != JsonValueKind.Array) throw new JsonException();
            return string.Concat(root[0].EnumerateArray().Select(segment =>
                segment.ValueKind == JsonValueKind.Array && segment.GetArrayLength() > 0 && segment[0].ValueKind == JsonValueKind.String
                    ? segment[0].GetString() : throw new JsonException()));
        }
        return string.Concat(root[0].GetProperty("translations").EnumerateArray()
            .Where(segment => segment.GetProperty("to").GetString() == "zh-Hans")
            .Select(segment => segment.GetProperty("text").GetString()));
    }
}
