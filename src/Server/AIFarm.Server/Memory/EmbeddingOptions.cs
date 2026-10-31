namespace AIFarm.Server.Memory;

/// <summary>
/// The "Embeddings" configuration section: an OpenAI-compatible embeddings API (Ollama by
/// default, as in compose.yaml). The model must produce
/// <see cref="Data.FarmDbContext.EmbeddingDimensions"/>-long vectors.
/// </summary>
public sealed class EmbeddingOptions
{
	public const string Section = "Embeddings";

	public Uri Endpoint { get; set; } = new("http://localhost:11434/v1");

	public string Model { get; set; } = "nomic-embed-text";

	/// <summary>API key for the endpoint; Ollama ignores it but the client needs one.</summary>
	public string ApiKey { get; set; } = "ollama";

	/// <summary>
	/// Ask the model for this many dimensions (for models that can shorten their output, like
	/// OpenAI's text-embedding-3). Leave unset for models with a fixed size.
	/// </summary>
	public int? Dimensions { get; set; }
}
