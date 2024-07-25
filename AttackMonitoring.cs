using Disqord;
using Disqord.Bot.Hosting;
using Disqord.Rest;
using System.Text.Json;
using System.Net;
using Disqord.Webhook;
using Serilog;

public class AttackMonitor : DiscordBotService {
	public class Attack {
		public int Id { get; set; }
		public string ?AttackId { get; set; }
		public string ?Status { get; set; }
		public string ?IpAddress { get; set; }
		public string ?StartTime { get; set; }
		public int Duration { get; set; }
		public int MaxPeak { get; set; }
		public int Pps { get; set; }
		public int Volume { get; set; }
		public string ?Description { get; set; }
	}

	public class ApiResponseData {
		public List<Attack> ?Attacks { get; set; }
	}

	public class ApiResponse {
		public ApiResponseData ?Data { get; set; }
		public bool Success { get; set; }
	}

	static bool isFirstRun = true;

	private static string authToken = "replace with your own auth token, u can get it here: https://royalehosting.net/dashboard/details -> Create New Api Key";
	static Dictionary<string, string> previousAttackStatuses = new Dictionary<string, string>();

	//yoinked from the SDK because all SDK functions to check for success are gay and always do a throw which is not what i want
	public static bool IsSuccessStatusCode(HttpStatusCode _statusCode) {
		return ((int)_statusCode >= 200) && ((int)_statusCode <= 299);
	}

	static async Task PollApiDataAsync()
	{
		var client = new HttpClient();
		var request = new HttpRequestMessage(HttpMethod.Get, "https://shield.royalehosting.net/api/v2/attacks?chunk=0");
		request.Headers.Add("token", authToken);

		var response = await client.SendAsync(request);

		if (!IsSuccessStatusCode(response.StatusCode) || response is null) {
			Console.WriteLine("PollApiData Request did not succeed returning...");
			return;
		}

		var responseContent = await response.Content.ReadAsStringAsync();

		using (JsonDocument jsonDocument = JsonDocument.Parse(responseContent)) {
			var attacksArray = jsonDocument.RootElement.GetProperty("data").GetProperty("attacks");

			foreach (var attackJson in attacksArray.EnumerateArray()) {
				var attack = new Attack {
					AttackId = attackJson.GetProperty("attack_id").GetString()!,
					Status = attackJson.GetProperty("status").GetString()!,
					IpAddress = attackJson.GetProperty("dest").GetString()!,
					StartTime = attackJson.GetProperty("start_time").GetString(),
					Duration = attackJson.GetProperty("duration").GetInt32(),
					MaxPeak = attackJson.GetProperty("mbps").GetInt32(),
					Pps = attackJson.GetProperty("pps").GetInt32(),
					Volume = attackJson.GetProperty("volume").GetInt32(),
					Description = attackJson.GetProperty("description").GetString()!
				};

				if (isFirstRun) {
					// Cache the current status
					previousAttackStatuses[attack.AttackId] = attack.Status;

					// On the first run, check if there are ongoing attacks that started while our Monitor wasnt running,
					// i know this is hacky and there is better ways to do this but i couldnt care less
					if (attack.Status != "end") {
						await SendDiscordEmbedAsync(attack);
					}
				}
				else {
					// Check if this attack is new or has a status change
					if (!previousAttackStatuses.ContainsKey(attack.AttackId) || previousAttackStatuses[attack.AttackId] != attack.Status) {
						// Cache the current status
						previousAttackStatuses[attack.AttackId] = attack.Status;

						await SendDiscordEmbedAsync(attack);
					}
				}
			}
			isFirstRun = false;
		}
	}


    static async Task SendDiscordEmbedAsync(Attack attack) {
        IWebhookClient client = Factory.CreateClient("your webhook link goes here");

        var embed = new LocalEmbed();

		// Set up our Embeds based on the Attack Status
		switch (attack.Status)
		{
			case "start":
				{ 
             embed.WithColor(16735232)
				     .WithTitle("Attack Detected\n\nDetails:")
				     .WithDescription($"**Attack ID:** {attack.AttackId}")
				     .AddField("Targeted IP", attack.IpAddress, true)
				     .AddField("Status:", attack.Status, true)
				     .AddField("Start Time", $"{attack.StartTime}", true)
				     .AddField("What now?", "Our server is currently transitioning into mitigation mode in response to this DDoS attack.");
				}
				break;

			case "ongoing":
				{
					   embed.WithColor(16711680)
				     .WithTitle("Attack Status Change\n\nDetails:")
				     .WithDescription($"**Attack ID:** {attack.AttackId}")
				     .AddField("Targeted IP", attack.IpAddress, true)
				     .AddField("Status:", attack.Status, true)
				     .AddField("Start Time", $"**{attack.StartTime}**", true)
				     .AddField("Network Stats:", $"{attack.MaxPeak} Mbits / {attack.MaxPeak / 1000} Gbits / {attack.Pps} PPS")
				     .AddField("What now?", "Our server is currently operating in mitigation mode, efficiently filtering out malicious traffic.");
				}
				break;

			case "end":
				{
				    embed.WithColor(4062976)
					 .WithTitle("Attack Ended\n\nDetails:")
					 .WithDescription($"**Attack ID:** {attack.AttackId}")
					 .AddField("Targeted IP", attack.IpAddress, true)
					 .AddField("Start Time", $"{attack.StartTime}", true)
					 .AddField("Attack Duration:", $"{attack.Duration} seconds", true)
					 .AddField("Attack Stats:", $"Max Peak: {attack.MaxPeak} Mbps/ {attack.MaxPeak / 1000} Gbps & {attack.Pps} PPS\nAttack Traffic Volume: {attack.Volume / 1000} GB")
					 .AddField("Attack Vector:", attack.Description)
					 .AddField("What now?", "Our server has successfully ceased detecting malicious traffic and has exited mitigation mode.");
				}
				break;

			default:
				{
					// Handle other statuses or provide a default template
					Console.WriteLine($"Unknown attack status: {attack.Status}");
					return;
				}
		}

		// finally try to send that embed to discord
		try {
			await client!.ExecuteAsync(new LocalWebhookMessage().WithEmbeds(embed).WithAuthorName("Attack Monitoring").WithAuthorAvatarUrl("replace me"));
		}
		catch (Exception e) {
			Console.WriteLine($"Error during sending Attack Webhook: {e.Message}");
		}
	}
	
	protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        try {
            await Client.WaitUntilReadyAsync(stoppingToken);
            if (stoppingToken.IsCancellationRequested)
                return;

            Console.WriteLine("Attack Monitor Started!");

            // Schedule polling every minute
            var pollingTimer = new System.Timers.Timer(60 * 1000);
            pollingTimer.Elapsed += async (sender, e) => await PollApiDataAsync();
            pollingTimer.AutoReset = true;
            pollingTimer.Start();

            // Perform initial polling
            await PollApiDataAsync();
        }
        catch (Exception ex) {
			Log.Logger.Error(ex, "Error in {Service}.", "Attack Monitor Service");
        }
	}

	// Custom comparer for Attack objects comparing, is done based on the AttackId
	public class AttackComparer : IEqualityComparer<Attack> {
		public bool Equals(Attack ?x, Attack ?y) {
			return x!.AttackId == y!.AttackId;
		}

		public int GetHashCode(Attack obj) {
			return obj?.AttackId?.GetHashCode() ?? 0;
		}
	}
}