namespace BugSwatter.AI;

/// <summary>A deterministic tool that an AI model may call through the generic tool-call loop</summary>
public interface IModelTool
{
    /// <summary>Model-facing function declaration</summary>
    ToolDefinition Definition { get; }

    /// <summary>Executes raw model-produced JSON arguments and returns content for the tool-result message</summary>
    string Execute(string argumentsJson);
}
