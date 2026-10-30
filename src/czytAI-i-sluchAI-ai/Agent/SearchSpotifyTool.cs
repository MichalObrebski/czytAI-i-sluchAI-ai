using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace czytAI_i_sluchAI_ai.Agent;

/// <summary>
/// A tool that allows the AI agent to search Spotify for a specific track.
/// </summary>
public sealed class SearchSpotifyTool : IAgentTool
{
    private readonly HttpClient _httpClient;
    private readonly string _accessToken;

    // Inject HttpClient and your Spotify OAuth Access Token here
    public SearchSpotifyTool(HttpClient httpClient, string accessToken)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _accessToken = accessToken ?? throw new ArgumentNullException(nameof(accessToken));
    }

    public string Name => "search_spotify_track";

    public string Description => "Searches Spotify for a track by name and returns the top result's details including artist, album, and Spotify URL.";

    public object ParametersSchema => new
    {
        type = "object",
        properties = new
        {
            query = new
            {
                type = "string",
                description = "The name of the song or track to search for, optionally including the artist."
            }
        },
        required = new[] { "query" }
    };

    public async Task<string> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        // 1. Extract the required parameter from the AI's arguments
        if (!arguments.TryGetProperty("query", out var queryElement) || string.IsNullOrWhiteSpace(queryElement.GetString()))
        {
            return "Error: Missing or empty 'query' parameter.";
        }

        string query = queryElement.GetString()!;

        try
        {
            // 2. Build the Spotify API Request
            // We restrict the search to type=track and limit=1 to keep the AI context window small
            string url = $"https://api.spotify.com/v1/search?q={Uri.EscapeDataString(query)}&type=track&limit=1";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);

            // Spotify requires a Bearer token for all requests
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

            // 3. Execute the request
            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return $"Spotify API call failed with status: {response.StatusCode}. Note: ensure the access token is valid and not expired.";
            }

            // 4. Return the JSON response to the agent
            // The agent will automatically parse this JSON to answer the user's questions
            string jsonResponse = await response.Content.ReadAsStringAsync(cancellationToken);
            return jsonResponse;
        }
        catch (Exception ex)
        {
            return $"An error occurred while calling the Spotify API: {ex.Message}";
        }
    }
}