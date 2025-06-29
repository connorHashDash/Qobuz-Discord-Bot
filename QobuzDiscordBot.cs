﻿using System.Diagnostics;
using System.Text;
using CliWrap;
using DotNetEnv;
using NetCord;
using NetCord.Gateway;
using NetCord.Gateway.Voice;
using NetCord.Logging;
using NetCord.Services;
using NetCord.Services.Commands;

Random random = new Random();
string _discordToken = "", _discordPrefix = "", _rootPath = Directory.GetCurrentDirectory(), _status = "Initialising";

if (!GetEnvironmentVariables())
    return;

GatewayClient client = new(new BotToken(_discordToken), new GatewayClientConfiguration
{
    Logger = new ConsoleLogger(),
    Intents = GatewayIntents.All
});

var commands = new CommandService<CommandContext>();

commands.AddCommand(["h", "help", "commands", "info"], () => "Discord Music Bot.\n\nCommands:\n\n" +
$"{_discordPrefix}h, {_discordPrefix}help, {_discordPrefix}commands, {_discordPrefix}info - Get bot information (current response).\n" +
$"{_discordPrefix}status - Get current bot status.\n\n" +
$"{_discordPrefix}p, {_discordPrefix}play *query* - Play a song using a song name and/or artist name.\n" +
$"{_discordPrefix}skip - Skips current track.\n" +
$"{_discordPrefix}stop - Stops current playback.");

commands.AddCommand(["status"], () => _status);



commands.AddCommand(["p", "play"], async (CommandContext context, [CommandParameter(Remainder = true)] string query) =>
{
    var guild = context.Guild!;
    if (!guild.VoiceStates.TryGetValue(context.User.Id, out var voiceState))
        return "You are not connected to a voice channel.";

    var client = context.Client;

    //todo: should check here if bot is already in channel

    var voiceClient = await client.JoinVoiceChannelAsync(
        guild.Id,
        voiceState.ChannelId.GetValueOrDefault(),
        new VoiceClientConfiguration
        {
            Logger = new ConsoleLogger()
        });

    await voiceClient.StartAsync();

    await voiceClient.EnterSpeakingStateAsync(new SpeakingProperties(SpeakingFlags.Microphone));

    await context.Message.ReplyAsync($"Downloading song \"{query}\"...");

    var dlResult = await DownloadTrack(query);

    //todo: error checking

    var trackPath = GetTrackPathById(dlResult.TrackId);

    if (string.IsNullOrWhiteSpace(trackPath))
        return "Failed to download and play track.";

    await context.Message.ReplyAsync($"Found and downloaded song: \"{Path.GetFileNameWithoutExtension(trackPath).Replace(dlResult.TrackId, "")}\" - if this is not correct, use {_discordPrefix}playr *query* (coming soon).");
    var outStream = voiceClient.CreateOutputStream();

    OpusEncodeStream stream = new(outStream, PcmFormat.Short, VoiceChannels.Stereo, OpusApplication.Audio);
    ProcessStartInfo startInfo = new("ffmpeg")
    {
        RedirectStandardOutput = true
    };
    var arguments = startInfo.ArgumentList;
    // arguments.Add("-reconnect");
    // arguments.Add("1");
    // arguments.Add("-reconnect_streamed");
    // arguments.Add("1");
    // arguments.Add("-reconnect_delay_max");
    // arguments.Add("5");
    arguments.Add("-i");
    arguments.Add(trackPath);
    // arguments.Add("-loglevel");
    // arguments.Add("-8");
    arguments.Add("-ac");
    arguments.Add("2");
    arguments.Add("-f");
    arguments.Add("s16le");
    arguments.Add("-ar");
    arguments.Add("48000");
    arguments.Add("pipe:1");
    var ffmpeg = Process.Start(startInfo);
    if (ffmpeg == null)
        return "Failed to start ffmpeg.";
    await context.Message.ReplyAsync($"Playing: \"{Path.GetFileNameWithoutExtension(trackPath).Replace(dlResult.TrackId, "")}\".");
    await ffmpeg.StandardOutput.BaseStream.CopyToAsync(stream);
    await stream.FlushAsync();
    return "";
});

commands.AddCommand(["skip"], () =>
{
    //todo
    return;
});

commands.AddCommand(["stop"], () =>
{
    //todo
    return;
});

commands.AddCommand(["kick", "leave", "quit", "q"], async (CommandContext context) =>
{
    var client = context.Client;
    var guild = context.Guild;

    // Get the current user (bot)
    var botUser = await client.Rest.GetCurrentUserAsync();

    // Check if the bot is in a voice channel
    if (!guild.VoiceStates.TryGetValue(botUser.Id, out var voiceState) || !voiceState.ChannelId.HasValue)
    {
        await context.Channel.SendMessageAsync("I'm not connected to any voice channel!");
        return;
    }

    // Leave the voice channel
    await client.UpdateVoiceStateAsync(new VoiceStateProperties(guild.Id, null));
    await context.Channel.SendMessageAsync("Left the voice channel!");
});
commands.AddModules(typeof(Program).Assembly);

client.MessageCreate += async message =>
{
    if (message.Author.IsBot || !message.Content.StartsWith(_discordPrefix))
        return;

    var result = await commands.ExecuteAsync(
        prefixLength: 1,
        new CommandContext(message, client)
    );

    _status = "Finished initialising";

    if (result is IFailResult fail)
        await message.ReplyAsync(fail.Message);
};

// client.VoiceStateUpdate += async (voiceState) =>
// {
//     //todo: implement way to leave when channel becomes empty.    
// };

await client.StartAsync();
await Task.Delay(-1);

#region Qobuz Methods

async Task<(bool Success, string TrackId, List<string> Errors, string Output)> DownloadTrack(string query)
{
    var stdErrBuffer = new StringBuilder();
    var newId = GenerateRandomString(8).ToLower();

    var result = await Cli.Wrap("qdl")
        .WithArguments([
            "lucky",
            "-t", "track",
            "--d", "./Music",
            "--no-cover",
            "--no-db",
            "--no-m3u",
            "-q", "5",
            "-ff", ".",
            "-tf", $"{"{tracktitle}"} {newId}",
            query
        ])
        .WithValidation(CommandResultValidation.None)
        .WithStandardErrorPipe(PipeTarget.ToStringBuilder(stdErrBuffer))
        .ExecuteAsync();

    return (true, newId, new List<string>(), stdErrBuffer.ToString());
}

#endregion
#region IO Music Utilities

string? GetTrackPathById(string fileId) => Directory.EnumerateFiles(Path.Combine(_rootPath, "Music"), "*", SearchOption.AllDirectories).FirstOrDefault(f => Path.GetFileName(f).Contains(fileId)) ?? null;

#endregion
#region Utilities

string GenerateRandomString(int length)
{
    const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    return new string(Enumerable.Repeat(chars, length)
        .Select(s => s[random.Next(s.Length)]).ToArray());
}

bool GetEnvironmentVariables()
{
    Env.Load();
    Env.TraversePath().Load();
    var tmpDiscordToken = Environment.GetEnvironmentVariable("DISCORD_TOKEN");
    var tmpDiscordPrefix = Environment.GetEnvironmentVariable("DISCORD_PREFIX");
    var errors = new List<string>();
    if (string.IsNullOrWhiteSpace(tmpDiscordToken))
        errors.Add("Error: Please provide a discord token in the .env file: DISCORD_TOKEN=\"your_token_here\"");
    if (string.IsNullOrWhiteSpace(tmpDiscordPrefix))
        errors.Add("Error: Please provide a discord prefix in the .env file: DISCORD_PREFIX=\"!\"");
    if (errors.Any())
    {
        Console.WriteLine(string.Join("\\n", errors));
        return false;
    }
    _discordToken = tmpDiscordToken!;
    _discordPrefix = tmpDiscordPrefix!;
    return true;
}

#endregion
