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
    Reasoning,
    Code,
    Vision,
    Image,
    Video,
    Ocr
}

/// <summary>
/// Capacité multimodale ciblée par le routeur full-local-first. Chaque capacité
/// est servie par un backend local dédié (Ollama vision, Tesseract, Qwen-Image,
/// CogVideoX), le cloud n'intervenant que si explicitement autorisé.
/// </summary>
public enum ModelCapability
{
    Text,
    ImageAnalysis,
    Ocr,
    ImageGeneration,
    VideoGeneration
}
