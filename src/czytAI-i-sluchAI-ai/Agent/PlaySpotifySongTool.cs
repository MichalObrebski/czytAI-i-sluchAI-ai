using System;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace czytAI_i_sluchAI_ai.Agent;

public sealed class PlaySpotifySongTool : IAgentTool
{
    private readonly HttpClient _httpClient;
    private readonly string _accessToken;

    public PlaySpotifySongTool(HttpClient httpClient, string accessToken)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _accessToken = accessToken ?? throw new ArgumentNullException(nameof(accessToken));
    }

    public string Name => "play_spotify_song";

    public string Description => "Searches for a song by title and plays it immediately in the local Spotify desktop app.";

    public object ParametersSchema => new
    {
        type = "object",
        properties = new
        {
            song_title = new
            {
                type = "string",
                description = "The title of the song to play. If the user only asked for a mood or genre, make up a fitting, popular song title yourself and put it here."
            }
        },
        required = new[] { "song_title" }
    };

    public async Task<string> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        if (!arguments.TryGetProperty("song_title", out var queryElement) || string.IsNullOrWhiteSpace(queryElement.GetString()))
        {
            return "Error: Missing 'song_title' parameter.";
        }

        string query = queryElement.GetString()!;

        try
        {
            string url = $"https://api.spotify.com/v1/search?q={Uri.EscapeDataString(query)}&type=track&limit=1";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return $"Spotify API call failed: {response.StatusCode}";

            string jsonResponse = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(jsonResponse);

            var items = doc.RootElement.GetProperty("tracks").GetProperty("items");
            if (items.GetArrayLength() == 0) return $"No song found for '{query}'.";

            var track = items[0];
            string uri = track.GetProperty("uri").GetString()!;
            string name = track.GetProperty("name").GetString()!;
            string artist = track.GetProperty("artists")[0].GetProperty("name").GetString()!;

            Process.Start(new ProcessStartInfo
            {
                FileName = uri,
                UseShellExecute = true
            });

            return $"Successfully launched Spotify and playing '{name}' by {artist}.";
        }
        catch (Exception ex)
        {
            return $"An error occurred while trying to play the song: {ex.Message}";
        }
    }
}