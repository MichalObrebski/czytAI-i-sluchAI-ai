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
        string model = "llama3.1")
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
        string model = "llama3.1")
    {
        _tools = tools.ToList();
        _endpoint = endpoint;
        _model = model;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

        string systemPrompt = @"You are an intelligent, concise desktop AI assistant with access to Spotify and screen-reading capabilities. Use your tools naturally only when the user's request requires them.

TOOL SELECTION GUIDELINES:
- `play_spotify_song`: Call this to play music. IMPORTANT: The ONLY parameter you can send is `song_title`. Never send 'genre', 'mood', or anything else. Deduce a fitting real song title silently, and pass ONLY that specific title to the tool.
- `read_screen_text`: Call this ONLY when the user references their current screen, document, or active window (e.g., 'this book', 'what am I reading'). Do not use this for general conversation.
- `search_spotify_track`: Call this only to answer trivia or track details without playing the song.

CHAINING ACTIONS (CRITICAL):
When the user asks you to read the screen and play a fitting song based on it, you MUST perform a **Two-Step Tool Chain**:
1. First, call `read_screen_text` and wait for the system response.
2. Once you receive the screen text, you MUST immediately make a **second tool call** to `play_spotify_song` using a valid `song_title` derived from that text. Never stop after just reading the screen.

CONVERSATION STYLE:
- Be highly concise. If a song is played via tool call, just reply: 'Playing [Song Name].'
- Do not output raw JSON, function schemas, or internal monologues.
- If the user asks a general question, just answer it normally without calling any tools.";
        _messages.Add(new ChatMessage("system", systemPrompt));
    }

    public async Task<string> SendAsync(string userPrompt, CancellationToken cancellationToken = default)
    {
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

        if (!message.TryGetProperty("tool_calls", out var toolCalls)
            || toolCalls.GetArrayLength() == 0)
        {
            return new ChatMessage("assistant", content);
        }

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