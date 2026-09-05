using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace p2poolmail
{
    [JsonSourceGenerationOptions(
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true)]
    [JsonSerializable(typeof(local_Stratum))]
    internal partial class LocalStratumJsonContext : JsonSerializerContext
    {
    }


    public sealed class local_Stratum
    {
        [JsonPropertyName("hashrate_15m")] public long Hashrate15m { get; set; }
        [JsonPropertyName("hashrate_1h")] public long Hashrate1h { get; set; }
        [JsonPropertyName("hashrate_24h")] public long Hashrate24h { get; set; }
        [JsonPropertyName("total_hashes")] public long TotalHashes { get; set; }
        [JsonPropertyName("total_stratum_shares")] public long TotalStratumShares { get; set; }
        [JsonPropertyName("last_share_found_time")] public long LastShareFoundTime { get; set; }
        [JsonPropertyName("shares_found")] public long SharesFound { get; set; }
        [JsonPropertyName("shares_failed")] public long SharesFailed { get; set; }
        [JsonPropertyName("average_effort")] public decimal AverageEffort { get; set; }
        [JsonPropertyName("current_effort")] public decimal CurrentEffort { get; set; }
        [JsonPropertyName("connections")] public int Connections { get; set; }
        [JsonPropertyName("incoming_connections")] public int IncomingConnections { get; set; }
        [JsonPropertyName("block_reward_share_percent")] public decimal BlockRewardSharePercent { get; set; }
        [JsonPropertyName("wallet")] public string Wallet { get; set; } = string.Empty;
        [JsonPropertyName("workers")] public string[] Workers { get; set; } = Array.Empty<string>();
    }

    internal class LocalStratum
    {
        public static string[] SplitFields(string value, char separator = ',')
            => (value ?? string.Empty).Split(separator, StringSplitOptions.TrimEntries);

        private static local_Stratum? LoadLocalStratum()
        {
            try
            {
                var json = File.ReadAllText(Settings.Current.p2pool_log.StratumApiDir);
                return JsonSerializer.Deserialize(json, LocalStratumJsonContext.Default.local_Stratum);
            }
            catch (Exception ex)
            {
                CommonHelper.WriteError($"Failed to load local stratum JSON from '{Settings.Current.p2pool_log.StratumApiDir}': {ex.Message}");
                return null;
            }
        }

        public static string StratumTxtFormat()
        {
            var msg = new StringBuilder();
            msg.Append(EmailIcons.Info).Append($" Hi,Instruction received at {DateTime.Now:HH:mm:ss}.").Append("\r\n");
            var stream = LoadLocalStratum();
            if (stream is null) return string.Empty;

            AppendStratumSummary(msg, stream);

            msg.Append("[ Name / IP / Uptime / Hashrate ]").Append("\r\n");
            foreach (var worker in stream.Workers)
            {
                // worker format: "ip:port,uptime,difficulty,hashrate,name"
                var field = SplitFields(worker);
                if (field.Length < 5)
                {
                    // Skip malformed lines instead of throwing: an exception here
                    // would make the mail that triggered this report retry forever.
                    CommonHelper.WriteWarn($"stratum: malformed worker line skipped: '{worker}'");
                    continue;
                }

                var workerAddress = field[0];
                var separatorIndex = workerAddress.LastIndexOf(':');
                if (separatorIndex > -1 && workerAddress.IndexOf(']') < separatorIndex)
                    workerAddress = workerAddress[..separatorIndex];

                long.TryParse(field[3], out var workHash);
                long.TryParse(field[1], out var uptime);
                TimeSpan ts = TimeSpan.FromSeconds(uptime);

                msg.Append(field[4]).Append("     "); // name
                msg.Append(workerAddress).Append("    "); // IP
                msg.Append($"{ts.Days}d {ts.Hours}h {ts.Minutes}m {ts.Seconds}s").Append("    "); // uptime
                msg.Append(workHash / 1000m).Append(" KH/s").Append("\r\n"); // hash
            }
            return msg.ToString();
        }

        public static string StratumTxtFormatLittle()
        {
            var msg = new StringBuilder();
            var stream = LoadLocalStratum();
            if (stream is null) return string.Empty;

            AppendStratumSummary(msg, stream);
            return msg.ToString();
        }

        // Shared hashrate/effort block used by both report formats.
        private static void AppendStratumSummary(StringBuilder msg, local_Stratum stream)
        {
            msg.Append(EmailIcons.Workers).Append(" Total worker: ").Append(stream.Connections).Append("\r\n");
            msg.Append(" Hashrate_15m: ").Append(stream.Hashrate15m / 1000m).Append(" KH/s").Append("\r\n");
            msg.Append(" Hashrate_1h: ").Append(stream.Hashrate1h / 1000m).Append(" KH/s").Append("\r\n");
            msg.Append(" Hashrate_24h: ").Append(stream.Hashrate24h / 1000m).Append(" KH/s").Append("\r\n");
            msg.Append(" Average effort: ").Append(stream.AverageEffort).Append("%\r\n");
            msg.Append(" Current effort: ").Append(stream.CurrentEffort).Append("%\r\n");
        }
    }
}