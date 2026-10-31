using System.Text.Json;
using AIFarm.Server.Game;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AIFarm.Server.Tests;

/// <summary>What the brain sends to Ollama and to Azure AI Foundry, seen from a stand-in server.</summary>
public sealed class BrainProviderTests
{
	private sealed record Seen(HttpMethod Method, Uri Uri, Dictionary<string, string> Headers, JsonElement Body);

	/// <summary>An endpoint that records requests and answers with a chat completion.</summary>
	private sealed class Recorder : HttpMessageHandler
	{
		public List<Seen> Requests { get; } = [];

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			var body = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
			Requests.Add(new Seen(request.Method, request.RequestUri!,
				request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value)), JsonDocument.Parse(body).RootElement.Clone()));
			return new HttpResponseMessage
			{
				Content = new StringContent("""
					{"id":"c1","object":"chat.completion","created":1,"model":"farmer","choices":[{"index":0,"finish_reason":"stop",
					"message":{"role":"assistant","content":"The cows are fine."}}],"usage":{"prompt_tokens":5,"completion_tokens":5,"total_tokens":10}}
					""", System.Text.Encoding.UTF8, "application/json"),
			};
		}
	}

	private static async Task<(Seen Request, string Reply)> Ask(BrainOptions settings, ChatOptions? chatOptions = null)
	{
		var recorder = new Recorder();
		using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
		using var chat = BrainClient.Create(settings, services, new HttpClient(recorder));

		var reply = await chat.GetResponseAsync("How are the cows?", chatOptions);

		return (Assert.Single(recorder.Requests), reply.Text);
	}

	[Fact]
	public async Task AzureFoundryUsesTheDeploymentAndTheKeyOnTheOpenAiRoute()
	{
		var settings = new BrainOptions
		{
			Provider = BrainProvider.AzureFoundry,
			Endpoint = new Uri("https://farm-resource.openai.azure.com/"),
			Model = "farmer-gpt",
			ApiKey = "secret-azure-key",
		};

		var (request, reply) = await Ask(settings, new ChatOptions { Temperature = 0.6f });

		Assert.Equal("The cows are fine.", reply);
		Assert.Equal("https://farm-resource.openai.azure.com/openai/v1/chat/completions", request.Uri.GetLeftPart(UriPartial.Path));
		Assert.Equal("Bearer secret-azure-key", request.Headers["authorization"]);
		Assert.Equal("farmer-gpt", request.Body.GetProperty("model").GetString()); // the deployment name
		Assert.Equal(0.6, request.Body.GetProperty("temperature").GetDouble(), 3);
	}

	[Theory]
	[InlineData("https://farm.services.ai.azure.com", "https://farm.services.ai.azure.com/openai/v1/")]
	[InlineData("https://farm.services.ai.azure.com/api/projects/farm", "https://farm.services.ai.azure.com/api/projects/farm/openai/v1/")]
	[InlineData("https://farm.openai.azure.com/openai/v1/", "https://farm.openai.azure.com/openai/v1/")]
	[InlineData("https://farm.openai.azure.com/openai/v1", "https://farm.openai.azure.com/openai/v1")]
	public void AzureEndpointsGetTheOpenAiRouteWithoutDoublingIt(string given, string expected) =>
		Assert.Equal(expected, BrainClient.BaseAddress(new BrainOptions { Provider = BrainProvider.AzureFoundry, Endpoint = new Uri(given) }).AbsoluteUri);

	private const string PastedEndpoint = "https://adams-ai-bots.services.ai.azure.com/openai/v1/responses";

	[Fact]
	public void APastedResponsesUrlSelectsTheResponsesApiAndKeepsTheBaseClean()
	{
		var settings = new BrainOptions { Provider = BrainProvider.AzureFoundry, Endpoint = new Uri(PastedEndpoint) };

		Assert.Equal(BrainApi.Responses, BrainClient.ApiFor(settings));
		Assert.Equal("https://adams-ai-bots.services.ai.azure.com/openai/v1", BrainClient.BaseAddress(settings).AbsoluteUri);
		// An explicit choice wins; a plain endpoint means chat completions.
		Assert.Equal(BrainApi.ChatCompletions, BrainClient.ApiFor(new BrainOptions { Endpoint = new Uri(PastedEndpoint), Api = BrainApi.ChatCompletions }));
		Assert.Equal(BrainApi.ChatCompletions, BrainClient.ApiFor(new BrainOptions { Endpoint = new Uri("https://x.openai.azure.com/") }));
		Assert.Equal("https://x.openai.azure.com/openai/v1/", BrainClient.BaseAddress(new BrainOptions
		{
			Provider = BrainProvider.AzureFoundry, Endpoint = new Uri("https://x.openai.azure.com/chat/completions"),
		}).AbsoluteUri.Replace("//openai", "/openai"));
	}

	/// <summary>An endpoint that answers the Responses API: first asking for a tool, then with text.</summary>
	private sealed class ResponsesRecorder : HttpMessageHandler
	{
		public List<Seen> Requests { get; } = [];

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			var body = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
			Requests.Add(new Seen(request.Method, request.RequestUri!,
				request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value)), JsonDocument.Parse(body).RootElement.Clone()));
			var first = Requests.Count == 1;
			var output = first
				? """{"type":"function_call","id":"fc_1","call_id":"call_1","name":"count_cows","arguments":"{}","status":"completed"}"""
				: """{"type":"message","id":"msg_1","role":"assistant","status":"completed","content":[{"type":"output_text","text":"There are 3 cows.","annotations":[]}]}""";
			return new HttpResponseMessage
			{
				Content = new StringContent(
					"{\"id\":\"resp_" + Requests.Count + "\",\"object\":\"response\",\"created_at\":1,\"status\":\"completed\",\"model\":\"gpt-5-farmer\"," +
					"\"output\":[" + output + "],\"parallel_tool_calls\":true,\"tools\":[],\"tool_choice\":\"auto\"," +
					"\"usage\":{\"input_tokens\":5,\"output_tokens\":5,\"total_tokens\":10}}",
					System.Text.Encoding.UTF8, "application/json"),
			};
		}
	}

	[Fact]
	public async Task TheResponsesApiRunsToolCallsOnAzureFoundry()
	{
		var settings = new BrainOptions
		{
			Provider = BrainProvider.AzureFoundry, Endpoint = new Uri(PastedEndpoint), Model = "gpt-5-farmer", ApiKey = "secret-azure-key",
			Temperature = null,
		};
		var recorder = new ResponsesRecorder();
		using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
		using var chat = BrainClient.Create(settings, services, new HttpClient(recorder));
		var counted = 0;
		var tool = AIFunctionFactory.Create(() => { counted++; return 3; }, "count_cows", "How many cows there are.");

		var reply = await chat.GetResponseAsync("How many cows?", new ChatOptions { Tools = [tool] });

		Assert.Equal("There are 3 cows.", reply.Text);
		Assert.Equal(1, counted); // the model asked for the tool, the server ran it and sent the result back
		Assert.Equal(2, recorder.Requests.Count);
		Assert.All(recorder.Requests, r =>
		{
			Assert.Equal("https://adams-ai-bots.services.ai.azure.com/openai/v1/responses", r.Uri.GetLeftPart(UriPartial.Path));
			Assert.Equal("Bearer secret-azure-key", r.Headers["authorization"]);
			Assert.Equal("gpt-5-farmer", r.Body.GetProperty("model").GetString());
			Assert.False(r.Body.TryGetProperty("temperature", out _));
		});
		Assert.Contains("function_call_output", recorder.Requests[1].Body.GetRawText());
	}

	[Fact]
	public void OllamaEndpointsAreUsedAsGiven() =>
		Assert.Equal("http://gpu-box:11434/v1", BrainClient.BaseAddress(new BrainOptions { Endpoint = new Uri("http://gpu-box:11434/v1") }).AbsoluteUri);

	[Fact]
	public async Task ReasoningModelsGetNoTemperatureWhenItIsLeftEmpty()
	{
		var settings = new BrainOptions
		{
			Provider = BrainProvider.AzureFoundry, Endpoint = new Uri("https://farm.openai.azure.com"), Model = "gpt-5-farmer", ApiKey = "k",
			Temperature = null,
		};

		var (request, _) = await Ask(settings, new ChatOptions { Temperature = settings.Temperature });

		Assert.False(request.Body.TryGetProperty("temperature", out _));
	}

	[Fact]
	public async Task OllamaStillGetsItsModelAndTheOllamaKey()
	{
		var settings = new BrainOptions { Endpoint = new Uri("http://gpu-box:11434/v1"), Model = "gemma4:12b" };

		var (request, _) = await Ask(settings);

		Assert.Equal("http://gpu-box:11434/v1/chat/completions", request.Uri.GetLeftPart(UriPartial.Path));
		Assert.Equal("gemma4:12b", request.Body.GetProperty("model").GetString());
		Assert.Equal("Bearer ollama", request.Headers["authorization"]);
	}

	[Fact]
	public void WithoutAKeyAzureSignsInWithEntraId()
	{
		var settings = new BrainOptions
		{
			Provider = BrainProvider.AzureFoundry, Endpoint = new Uri("https://farm.openai.azure.com"), Model = "farmer-gpt", ApiKey = "",
		};
		using var services = new ServiceCollection().AddLogging().BuildServiceProvider();

		using var chat = BrainClient.Create(settings, services); // builds; the token is fetched on the first request

		Assert.NotNull(chat);
	}

	[Fact]
	public void BlankComposeValuesMeanEntraSignInAndNoTemperature()
	{
		// What compose passes when .env sets BRAIN_API_KEY= and BRAIN_TEMPERATURE= (both empty).
		var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
		{
			["Brain:Provider"] = "AzureFoundry",
			["Brain:Endpoint"] = "https://farm.openai.azure.com",
			["Brain:Model"] = "gpt-5-farmer",
			["Brain:ApiKey"] = "",
			["Brain:Temperature"] = "",
			["Brain:Api"] = "",
		}).Build();

		var settings = configuration.GetSection(BrainOptions.Section).Get<BrainOptions>()!;

		Assert.Equal(BrainProvider.AzureFoundry, settings.Provider);
		Assert.Equal("", settings.ApiKey);
		Assert.Null(settings.Temperature);
		Assert.Null(settings.Api); // so the endpoint decides
	}
}

/// <summary>The three reply options are pulled out of whatever the model wraps them in.</summary>
public sealed class ReplyParsingTests
{
	private static readonly string[] Expected = ["Good to hear!", "Which cow is yours?", "Need a hand?"];

	[Theory]
	[InlineData("""{"replies": ["Good to hear!", "Which cow is yours?", "Need a hand?"]}""")]
	[InlineData("Sure! Here you go:\n```json\n{\"replies\": [\"Good to hear!\", \"Which cow is yours?\", \"Need a hand?\"]}\n```\nAnything else?")]
	[InlineData("""{"Replies": ["Good to hear!", "Which cow is yours?", "Need a hand?"]}""")]
	[InlineData("1. Good to hear!\n2. Which cow is yours?\n3. Need a hand?")]
	[InlineData("- \"Good to hear!\"\n- \"Which cow is yours?\"\n- \"Need a hand?\"")]
	public void FindsThemInJsonCodeFencesOrLists(string answer) =>
		Assert.Equal(Expected, NpcBrain.ParseReplies(answer));

	[Fact]
	public void TooLongOptionsAreCutAndRepeatsDropped()
	{
		var parsed = NpcBrain.ParseReplies("""{"replies": ["Same", "Same", "Different", "%s"]}""".Replace("%s", new string('x', 300)));

		Assert.Equal(["Same", "Different"], parsed.Take(2));
		Assert.Equal(3, parsed.Count);
		Assert.Equal(120, parsed[2].Length);
	}
}
