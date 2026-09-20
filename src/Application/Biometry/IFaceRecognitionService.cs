namespace JarvisAI.Application.Biometry;

/// <summary>
/// Represents an enrolled face in the database.
/// </summary>
public sealed class FaceEnrollment
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public float[] Embedding { get; set; } = Array.Empty<float>();
    public string? Notes { get; set; }
    public DateTime EnrolledAt { get; set; } = DateTime.UtcNow;
    public string? PhotoPath { get; set; }
}

/// <summary>
/// Result of a face recognition attempt.
/// </summary>
public sealed class FaceRecognitionResult
{
    public bool Success { get; set; }
    public string? DetectedName { get; set; }
    public string? FaceId { get; set; }
    public float Confidence { get; set; }
    public int FaceCount { get; set; }
    public string? Notes { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Face recognition service using FaceAiSharp (ONNX local).
/// No cloud, no external API. 100% local processing.
/// </summary>
public interface IFaceRecognitionService
{
    /// <summary>
    /// Check if the service is available (models loaded, webcam accessible).
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Capture a frame from the webcam and detect/recognize faces.
    /// </summary>
    Task<FaceRecognitionResult> RecognizeAsync(CancellationToken ct = default);

    /// <summary>
    /// Capture a frame and return the raw face detections with embeddings.
    /// </summary>
    Task<IReadOnlyList<FaceEnrollment>> DetectFacesAsync(CancellationToken ct = default);

    /// <summary>
    /// Enroll a new face with a given name from the current webcam frame.
    /// </summary>
    Task<FaceRecognitionResult> EnrollAsync(string name, string? notes = null, CancellationToken ct = default);

    /// <summary>
    /// List all enrolled faces.
    /// </summary>
    IReadOnlyList<FaceEnrollment> ListEnrolled();

    /// <summary>
    /// Remove an enrolled face by ID or name.
    /// </summary>
    bool Forget(string idOrName);

    /// <summary>
    /// Describe what the webcam sees (faces, positions, glasses, etc.).
    /// </summary>
    Task<string> DescribeSceneAsync(CancellationToken ct = default);
}
