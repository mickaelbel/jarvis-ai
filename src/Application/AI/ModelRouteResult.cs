using System;

namespace JarvisAI.Application.AI;

public sealed record ModelRouteResult(
    string Model,
    ModelProfile Profile,
    string Reason,
    DateTime Timestamp);

public enum ModelProfile
{
    Fast,
    Reasoning
}
