namespace FsCopilot.Connection;

[AttributeUsage(AttributeTargets.Field)]
public class SimVarAttribute(string name, string units, int order) : Attribute
{
    public string Name { get; } = name;
    public string Units { get; } = units;
    public int Order { get; } = order;
}