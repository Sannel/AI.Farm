using System.ClientModel;
using System.ClientModel.Primitives;
using Azure.Identity;
using Microsoft.Extensions.AI;
using OpenAI;

namespace AIFarm.Server.Game;

/// <summary>Builds the chat client that lets a language model play the NPCs, for the configured provider.</summary>
public static class BrainClient
{
	/// <summary>The scope Azure AI Foundry / Azure OpenAI accept Entra ID tokens for.</summary>
	public const string AzureScope = "https://cognitiveservices.azure.com/.default";

	private static readonly string[] RequestSuffixes = ["/responses", "/chat/completions"];

	/// <summary>The endpoint with a pasted request path ("/responses", "/chat/completions") taken off.</summary>
	private static string WithoutRequestPath(BrainOptions settings)
	{
		var endpoint = settings.Endpoint.GetLeftPart(UriPartial.Path).TrimEnd('/');
		foreach (var suffix in RequestSuffixes)
		{
			if (endpoint.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
			{
				return endpoint[..^suffix.Length];
			}
		}
		return settings.Endpoint.AbsoluteUri;
	}

	/// <summary>The request style in use: the configured one, else what the endpoint says it is.</summary>
	public static BrainApi ApiFor(BrainOptions settings) =>
		settings.Api ?? (settings.Endpoint.GetLeftPart(UriPartial.Path).TrimEnd('/').EndsWith("/responses", StringComparison.OrdinalIgnoreCase)
			? BrainApi.Responses
			: BrainApi.ChatCompletions);

	/// <summary>The base URL requests go to: Azure's endpoints get its OpenAI-compatible "/openai/v1/" route.</summary>
	public static Uri BaseAddress(BrainOptions settings)
	{
		var endpoint = WithoutRequestPath(settings);
		if (settings.Provider != BrainProvider.AzureFoundry || endpoint.Contains("/openai/v1", StringComparison.OrdinalIgnoreCase))
		{
			return new Uri(endpoint);
		}
		return new Uri(endpoint.TrimEnd('/') + "/openai/v1/");
	}

	/// <summary>The OpenAI client for the provider; a test can pass its own HTTP client to see the requests.</summary>
	public static OpenAIClient CreateClient(BrainOptions settings, HttpClient? http = null)
	{
		var options = new OpenAIClientOptions
		{
			Endpoint = BaseAddress(settings),
			NetworkTimeout = settings.TurnTimeout, // a model on a CPU can take a while to answer
		};
		if (http is not null)
		{
			options.Transport = new HttpClientPipelineTransport(http);
		}
		if (settings.Provider == BrainProvider.AzureFoundry && string.IsNullOrWhiteSpace(settings.ApiKey))
		{
			// No key: sign in with Entra ID, whichever way this machine or container has to.
#pragma warning disable OPENAI001 // OpenAI's token-based sign-in is marked experimental
			return new OpenAIClient(new BearerTokenPolicy(new DefaultAzureCredential(), AzureScope), options);
#pragma warning restore OPENAI001
		}
		return new OpenAIClient(new ApiKeyCredential(settings.ApiKey), options);
	}

	/// <summary>
	/// An error from the model API in words: for failed HTTP calls, the status and the provider's
	/// own explanation (e.g. "DeploymentNotFound"). The API key is blanked out if it appears.
	/// </summary>
	public static string Describe(Exception error, BrainOptions settings)
	{
		var text = error.Message;
		if (error is ClientResultException failed && failed.GetRawResponse() is { } response)
		{
			var body = response.Content.ToString();
			text = $"{text} {(body.Length > 600 ? body[..600] + "…" : body)}".Trim();
		}
		return settings.ApiKey.Length > 4 ? text.Replace(settings.ApiKey, "***") : text;
	}

	/// <summary>The chat client the brain uses: the provider's model, able to call tools (up to MaxSteps in a turn).</summary>
	public static IChatClient Create(BrainOptions settings, IServiceProvider services, HttpClient? http = null)
	{
		var client = CreateClient(settings, http);
#pragma warning disable OPENAI001 // OpenAI's Responses client is marked experimental
		var model = ApiFor(settings) == BrainApi.Responses
			? client.GetResponsesClient().AsIChatClient(settings.Model)
			: client.GetChatClient(settings.Model).AsIChatClient();
#pragma warning restore OPENAI001
		return new ChatClientBuilder(model)
			.UseFunctionInvocation(services.GetService<ILoggerFactory>(),
				invoker => invoker.MaximumIterationsPerRequest = settings.MaxSteps)
			.Build(services);
	}
}
