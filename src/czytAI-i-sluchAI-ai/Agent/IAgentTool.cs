using System.Text.Json;

namespace czytAI_i_sluchAI_ai.Agent;

public interface IAgentTool
{
    string Name { get; }
    string Description { get; }
    object ParametersSchema { get; }
    Task<string> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken);
}
