using System.Collections.Concurrent;

using Serilog;
using Microsoft.Extensions.Configuration;

using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TgMessage = Telegram.Bot.Types.Message;
using TgChat = Telegram.Bot.Types.Chat;
using TgUpdateType = Telegram.Bot.Types.Enums.UpdateType;

using VkNet;
using VkNet.Enums.Filters;
using VkNet.Model;
using VkNet.Exception;
using VkUpdateType = VkNet.Model.UpdateType;
using VkNet.Enums.StringEnums;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateLogger();
    
IConfiguration configuration = new ConfigurationBuilder()
    .AddUserSecrets(typeof(Program).Assembly)
    .AddJsonFile(
        path: "appsettings.json", 
        optional: true, 
        reloadOnChange: true)
    .Build();


var vkApi = new VkApi();
var vkGroupUpdateWatcher = new VkGroupUpdateWatcher(vkApi);
var tgBot = new TelegramBotClient(
    new TelegramBotClientOptions(
        configuration["Telegram:Token"] ?? string.Empty));

await vkApi.AuthorizeAsync(new ApiAuthParams()
{
    Login = configuration["Vk:Login"],
    Password = configuration["Vk:Password"],
    ApplicationId = ulong.Parse(configuration["Vk:ApplicationId"]!),
    Settings = Settings.All
});

tgBot.StartReceiving(new TelegramBotUpdateHandler(tgBot, vkApi, vkGroupUpdateWatcher));

var CancellationTokenSource = new CancellationTokenSource();

await vkGroupUpdateWatcher.RunAsync(CancellationTokenSource.Token);


class TelegramBotUpdateHandler : IUpdateHandler
{
    private const string CommandPrefix = "/";
    private const string BindGroupCommand = $"{CommandPrefix}Bind";

    private readonly ConcurrentDictionary<long, ConcurrentBag<long>> _groupToChatsBindings = new();
    private readonly ITelegramBotClient _tgBot;
    private readonly VkApi _vkApi;
    private readonly VkGroupUpdateWatcher _vkGroupUpdateWatcher;

    public TelegramBotUpdateHandler(
        ITelegramBotClient tgBot, 
        VkApi vkApi, 
        VkGroupUpdateWatcher vkGroupUpdateWatcher)
    {
        _tgBot = tgBot;
        _vkApi = vkApi;
        _vkGroupUpdateWatcher = vkGroupUpdateWatcher;
        _vkGroupUpdateWatcher.AddOnNewPostHandler(OnNewPostAsync);
    }

    public Task HandlePollingErrorAsync(
        ITelegramBotClient botClient, 
        Exception exception, 
        CancellationToken cancellationToken)
    {
        Log.Error(exception, "Not expected exception.");

        return Task.CompletedTask;
    }

    public async Task HandleUpdateAsync(
        ITelegramBotClient botClient, 
        Update update, 
        CancellationToken cancellationToken)
    {
        if (update.Type is not TgUpdateType.Message ||
            update.Message is null)
            return;

        TgMessage message = update.Message;

        if (message.Type == MessageType.Text &&
            message.Text!.StartsWith(CommandPrefix))
        {
            await HandleCommandAsync(botClient, message, cancellationToken);
            return;
        }
    }

    private async Task HandleCommandAsync(
        ITelegramBotClient botClient,
        TgMessage message, 
        CancellationToken cancellationToken)
    {
        string[] splitedText = message.Text!.Split(' ');

        string command = splitedText[0];

        string[] args = splitedText[1..];

        switch (command)
        {
            case BindGroupCommand:
                await BindGroupAsync(botClient, message.Chat, args[0], cancellationToken);
                return;
                
        }
    }

    private async Task BindGroupAsync(
        ITelegramBotClient botClient,
        TgChat chat,
        string groupId,
        CancellationToken cancellationToken)
    {
        Group? group = null;
        try
        {
            group = (await _vkApi.Groups.GetByIdAsync(null, groupId, GroupsFields.All, token: cancellationToken)).FirstOrDefault();
            
        }
        catch (ParameterMissingOrInvalidException) { }

        if (cancellationToken.IsCancellationRequested)
            return;

        if (group is null)
        {
            await botClient.SendTextMessageAsync(
                chatId: chat.Id, 
                text: $"Group with '{groupId}' id not foudh.", 
                cancellationToken: cancellationToken);

            return;
        }

        _groupToChatsBindings.AddOrUpdate(
            group.Id, 
            groupId => new ConcurrentBag<long>(new long[]{ 1, 1}), 
            (groupId, chatIds) => 
            { 
                chatIds.Add(chat.Id); 
                return chatIds;
            });

        await _vkGroupUpdateWatcher.AddOrUpdateGroupForWatchingAsync(group.Id, cancellationToken);

        await botClient.SendTextMessageAsync(chat, $"This Chat Binded To Group: {group.Name}.", cancellationToken: cancellationToken);
    }

    private async Task OnNewPostAsync(long groupId, WallPost post)
    {
        ConcurrentBag<long>? chatIds; 

        if (!_groupToChatsBindings.TryGetValue(groupId, out chatIds))
            return;

        foreach (long chatId in chatIds)
        {
            await _tgBot.SendTextMessageAsync(chatId, post.Text);
        }
    }
}

public delegate Task NewPostHanblerAsyncDelegate(long groupId, WallPost post);

class VkGroupUpdateWatcher
{
    private readonly ConcurrentDictionary<long, string> _groupsToWatch = new();
    private readonly ConcurrentBag<NewPostHanblerAsyncDelegate> _newPostHandlers = new();
    private readonly VkApi _vkApi;

    public VkGroupUpdateWatcher(VkApi vkApi)
    {
        _vkApi = vkApi;
    }

    public async Task AddOrUpdateGroupForWatchingAsync(
        long groupId, 
        CancellationToken cancellationToken)
    {
        LongPollServerResponse response = 
            await _vkApi.Groups.GetLongPollServerAsync(
                groupId: (ulong)groupId, 
                token: cancellationToken);

        AddOrUpdateTs(groupId, response.Ts);
    }

    public void AddOnNewPostHandler(NewPostHanblerAsyncDelegate handler)
    {
        _newPostHandlers.Add(handler);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_groupsToWatch.IsEmpty)
            {
                await Task.Delay(1, cancellationToken);           
                continue;
            }

            foreach ((long groupId, string ts) in _groupsToWatch)
                await GetUpdatesAndHanbleAsync(groupId, ts, cancellationToken);
        }
    }

    private async Task GetUpdatesAndHanbleAsync(long groupId, string ts, CancellationToken cancellationToken)
    {
        BotsLongPollHistoryResponse response = await _vkApi.Groups
            .GetBotsLongPollHistoryAsync(new BotsLongPollHistoryParams
            {
                Ts = ts,
            }, token: cancellationToken);

        AddOrUpdateTs(groupId, ts);

        IEnumerable<WallPost> newPosts = response.Updates
            .Where(x => x.Type.Value is GroupUpdateType.WallPostNew)
            .Cast<WallPost>();

        foreach (WallPost post in newPosts)
            foreach (NewPostHanblerAsyncDelegate handler in _newPostHandlers)
                await handler(groupId, post);
    }

    private void AddOrUpdateTs(long groupId, string ts)
    {
        _groupsToWatch.AddOrUpdate(groupId, _ => ts, (_, _) => ts);
    }
}