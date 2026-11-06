using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace czytAI_i_sluchAI_ai.Agent;

public sealed class LocalAgent
{
    private readonly HttpClient _httpClient;
    private readonly IReadOnlyList<IAgentTool> _tools;
    private readonly string _endpoint;
    private readonly string _model;
    private readonly List<ChatMessage> _messages = [];

    public event Action<string>? ToolCalled;

    public static async Task<LocalAgent> CreateSpotifyAgentAsync(
        string clientId,
        string clientSecret,
        string endpoint = "http://localhost:11434/v1/chat/completions",
        string model = "granite4.2:3b")
    {
        using var authClient = new HttpClient();
        var tokenRequest = new HttpRequestMessage(HttpMethod.Post, "https://accounts.spotify.com/api/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                { "grant_type", "client_credentials" },
                { "client_id", clientId },
                { "client_secret", clientSecret }
            })
        };

        var response = await authClient.SendAsync(tokenRequest);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Failed to get Spotify token: {error}");
        }

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var accessToken = doc.RootElement.GetProperty("access_token").GetString()!;

        var spotifyClient = new HttpClient();
        var tools = new IAgentTool[]
        {
            new SearchSpotifyTool(spotifyClient, accessToken),
            new PlaySpotifySongTool(spotifyClient, accessToken),
            new ReadScreenTextTool(),
        };

        return new LocalAgent(tools, endpoint, model);
    }

    public LocalAgent(
        IEnumerable<IAgentTool> tools,
        string endpoint = "http://localhost:11434/v1/chat/completions",
        string model = "granite4.2:3b")
    {
        _tools = tools.ToList();
        _endpoint = endpoint;
        _model = model;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

        string systemPrompt = @"You are an intelligent, concise desktop AI assistant with access to Spotify and screen-reading capabilities. Use your tools naturally only when the user's request requires them.

VIBE-TO-SONG REFERENCE LIBRARY:
When matching a song to the user's screen or requested mood, strongly prefer using one of these proven tracks. Match the keywords to the text you read:

- Nature / Forest / Calm / Refreshing -> 'Holocene', 'Clair de Lune', 'Sunrise', 'Bloom', 'Wildflower'
- Epic / Battle / War / History / Action -> 'The Ecstasy of Gold', 'O Fortuna', 'Now We Are Free', 'Immigrant Song', 'Conquest of Paradise'
- Sci-Fi / Space / Cyberpunk / Tech / Future -> 'Blade Runner Blues', 'Cornfield Chase', 'Nightcall', 'Starboy', 'Genesis'
- Mystery / Suspense / Detective / Dark / Thriller -> 'Teardrop', 'Way Down We Go', 'Red Right Hand', 'Bury a Friend', 'Paint It, Black'
- Romance / Drama / Poetry / Emotional -> 'At Last', 'Sparks', 'Perfect', 'Make You Feel My Love', 'Yellow'
- Focus / Studying / Non-Fiction / Coding -> 'Weightless', 'Snowfall', 'Day One', 'Nuvole Bianche', 'Avril 14th'
- Happy / Upbeat / Comedy / Casual / Sunny -> 'Uptown Funk', 'Walking On Sunshine', 'Happy', 'Mr. Blue Sky', 'Levitating'
- Sad / Melancholy / Tragedy / Grief -> 'Someone Like You', 'Hurt', 'Fix You', 'The Sound of Silence', 'Mad World'
- Horror / Scary / Macabre / Gothic -> 'Tubular Bells', 'Halloween Theme', 'Suspiria', 'Lullaby', 'Psycho Theme'
- Fantasy / Magic / Mythology / Adventure -> 'Concerning Hobbits', 'The Dragonborn Comes', 'Toss A Coin To Your Witcher', 'Hedwig's Theme', 'May It Be'

TOOL SELECTION GUIDELINES:
- `play_spotify_song`: Call this to play music. IMPORTANT: The ONLY parameter you can send is `song_title`. Never send 'genre', 'mood', or anything else. Use the Vibe-to-Song Reference Library to silently pick a specific title, and pass ONLY that exact title to the tool.
- `read_screen_text`: Call this ONLY when the user references their current screen, document, or active window (e.g., 'this book', 'what am I reading'). Do not use this for general conversation.
- `search_spotify_track`: Call this only to answer trivia or track details without playing the song.

CHAINING ACTIONS (CRITICAL):
When the user asks you to read the screen and play a fitting song based on it, you MUST perform a **Two-Step Tool Chain**:
1. First, call `read_screen_text` and wait for the system response.
2. Once you receive the screen text, identify its keywords, find a matching song from the Vibe Library, and immediately make a **second tool call** to `play_spotify_song` using that `song_title`. Never stop after just reading the screen.

CONVERSATION STYLE:
- Be highly concise. If a song is played via tool call, just reply: 'Playing [Song Name].'
- Do not output raw JSON, function schemas, or internal monologues.
- If the user asks a general question, just answer it normally without calling any tools.";
        _messages.Add(new ChatMessage("system", systemPrompt));
    }

    public async Task<string> SendAsync(string userPrompt, CancellationToken cancellationToken = default)
    {
        var systemMessage = _messages.FirstOrDefault(m => m.Role == "system");
        _messages.Clear();
        if (systemMessage != null)
        {
            _messages.Add(systemMessage);
        }
        _messages.Add(new ChatMessage("user", userPrompt));

        // The while loop allows the AI to call a tool, read the result, and call ANOTHER tool.
        while (true)
        {
            // 1. Send the request using your custom HTTP method
            var responseDoc = await SendRequestAsync(cancellationToken);

            // 2. Parse the response into your custom ChatMessage record
            var assistantMessage = ReadAssistantMessage(responseDoc);
            _messages.Add(assistantMessage);

            // 3. If there is no tool call, the AI is done. Return the text.
            if (assistantMessage.ToolCall == null)
            {
                return assistantMessage.Content ?? string.Empty;
            }

            // 4. The AI wants to use a tool. Find it and execute it.
            ToolCalled?.Invoke(assistantMessage.ToolCall.Name); // Update UI if needed

            var tool = _tools.FirstOrDefault(t => t.Name == assistantMessage.ToolCall.Name);
            string result;

            if (tool != null)
            {
                try
                {
                    result = await tool.ExecuteAsync(assistantMessage.ToolCall.Arguments, cancellationToken);
                }
                catch (Exception ex)
                {
                    result = $"Tool execution error: {ex.Message}";
                }
            }
            else
            {
                result = $"Error: Tool '{assistantMessage.ToolCall.Name}' not found.";
            }

            // 5. Add the tool result to the history using your custom format
            _messages.Add(new ChatMessage(
                Role: "tool",
                Content: result,
                ToolCallId: assistantMessage.ToolCall.Id,
                ToolName: assistantMessage.ToolCall.Name));

            // The loop repeats so the AI can read the result and respond!
        }
    }

    private async Task<JsonDocument> SendRequestAsync(CancellationToken cancellationToken)
    {
        var request = new
        {
            model = _model,
            messages = _messages.Select(message => message.ToRequestObject()).ToArray(),
            tools = _tools.Select(t => new
            {
                type = "function",
                function = new
                {
                    name = t.Name,
                    description = t.Description,
                    parameters = t.ParametersSchema
                }
            }).ToArray(),
            tool_choice = "auto"
        };

        using var content = new StringContent(
            JsonSerializer.Serialize(request),
            Encoding.UTF8,
            "application/json");
        using var response = await _httpClient.PostAsync(_endpoint, content, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Local model request failed ({(int)response.StatusCode}): {responseBody}");
        }

        return JsonDocument.Parse(responseBody);
    }

    private static ChatMessage ReadAssistantMessage(JsonDocument response)
    {
        var message = response.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message");

        var content = message.TryGetProperty("content", out var contentElement)
            && contentElement.ValueKind != JsonValueKind.Null
            ? contentElement.GetString()
            : null;

        // 1. Standard API Tool Call Check (The Happy Path)
        if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.GetArrayLength() > 0)
        {
            var toolCall = toolCalls[0];
            var function = toolCall.GetProperty("function");
            return new ChatMessage(
                "assistant",
                content,
                new ToolCall(
                    toolCall.GetProperty("id").GetString() ?? "tool-call",
                    function.GetProperty("name").GetString()
                        ?? throw new InvalidOperationException("The model returned a tool call without a name."),
                    ParseArguments(function.GetProperty("arguments"))));
        }

        // 2. Fallback Parser: Catch leaked JSON in the text response
        if (!string.IsNullOrWhiteSpace(content) && content.Trim().StartsWith("{") && content.Contains("play_spotify_song"))
        {
            try
            {
                using var leakedJson = JsonDocument.Parse(content.Trim());
                var root = leakedJson.RootElement;

                string? songTitle = null;

                // Handle the specific hallucinated formats ("params" or "parameters")
                if (root.TryGetProperty("params", out var p) && p.TryGetProperty("song_title", out var st1))
                    songTitle = st1.GetString();
                else if (root.TryGetProperty("parameters", out var p2) && p2.TryGetProperty("song_title", out var st2))
                    songTitle = st2.GetString();

                if (!string.IsNullOrEmpty(songTitle))
                {
                    // Create a valid arguments JSON element for the execution loop
                    string validArgs = $"{{\"song_title\": \"{songTitle}\"}}";
                    using var argsDoc = JsonDocument.Parse(validArgs);

                    return new ChatMessage(
                        "assistant",
                        $"Playing '{songTitle}'.", // Replace the ugly JSON with a clean text response
                        new ToolCall(
                            "fallback-tool-call",
                            "play_spotify_song",
                            argsDoc.RootElement.Clone()));
                }
            }
            catch
            {
                // If it fails to parse, ignore it and let the raw text fall through to the user
            }
        }

        // 3. Normal conversational text
        return new ChatMessage("assistant", content);
    }

    private static JsonElement ParseArguments(JsonElement arguments)
    {
        if (arguments.ValueKind == JsonValueKind.String)
        {
            using var document = JsonDocument.Parse(arguments.GetString() ?? "{}");
            return document.RootElement.Clone();
        }

        return arguments.Clone();
    }

    private sealed record ChatMessage(
        string Role,
        string? Content,
        ToolCall? ToolCall = null,
        string? ToolCallId = null,
        string? ToolName = null)
    {
        public object ToRequestObject()
        {
            if (Role == "assistant" && ToolCall is not null)
            {
                return new
                {
                    role = Role,
                    content = Content,
                    tool_calls = new[]
                    {
                        new
                        {
                            id = ToolCall.Id,
                            type = "function",
                            function = new
                            {
                                name = ToolCall.Name,
                                arguments = ToolCall.Arguments.GetRawText()
                            }
                        }
                    }
                };
            }

            if (Role == "tool")
            {
                return new
                {
                    role = Role,
                    content = Content,
                    tool_call_id = ToolCallId
                };
            }

            return new { role = Role, content = Content };
        }
    }

    private sealed record ToolCall(string Id, string Name, JsonElement Arguments);
}