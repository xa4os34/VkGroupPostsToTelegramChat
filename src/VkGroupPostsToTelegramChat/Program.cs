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
            groupId => new ConcurrentBag<long>(new long[]{ chat.Id }), 
            (groupId, chatIds) => 
            { 
                chatIds.Add(chat.Id); 
                return chatIds;
            });

        await _vkGroupUpdateWatcher.AddOrUpdateGroupForWatchingAsync(group.Id, cancellationToken);

        await botClient.SendTextMessageAsync(chat, $"This Chat Binded To Group: {group.Name}.", cancellationToken: cancellationToken);
    }

    private async Task OnNewPostAsync(long groupId, Post post)
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

public delegate Task NewPostHanblerAsyncDelegate(long groupId, Post post);

class VkGroupUpdateWatcher
{
    private const int PostsPerRequest = 10;

    private readonly ConcurrentDictionary<long, long?> _groupsToWatch = new();
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
        WallGetObject wall = await _vkApi.Wall.GetAsync(new WallGetParams
        {
            OwnerId = -groupId,
            Offset = 0,
            Count = 1
        }, token: cancellationToken);

        Post? firstPost = wall.WallPosts.FirstOrDefault();

        AddOrUpdateGroupToWatch(groupId, firstPost?.Id);
    }

    public void AddOnNewPostHandler(NewPostHanblerAsyncDelegate handler)
    {
        _newPostHandlers.Add(handler);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(1));

            if (_groupsToWatch.IsEmpty)
                continue;

            foreach ((long groupId, long? lastPostId) in _groupsToWatch)
                await GetUpdatesAndHanbleAsync(groupId, lastPostId, cancellationToken);
        }
    }

    private async Task GetUpdatesAndHanbleAsync(long groupId, long? lastPostId, CancellationToken cancellationToken)
    {
        IEnumerable<Post> newPosts = await GetNewPostsAsync(groupId, lastPostId, cancellationToken);

        foreach (Post post in newPosts)
            foreach (NewPostHanblerAsyncDelegate handler in _newPostHandlers)
                await handler(groupId, post);
    }

    private async Task<IEnumerable<Post>> GetNewPostsAsync(long groupId, long? lastPostId, CancellationToken cancellationToken)
    {
        bool isLastPostEncountered = false;
        LinkedList<Post> newPosts = new();

        for (var offset = 0ul; !isLastPostEncountered; offset += PostsPerRequest)
        {
            WallGetObject wall = await _vkApi.Wall.GetAsync(new WallGetParams
            {
                OwnerId = -groupId,
                Offset = offset,
                Count = PostsPerRequest
            }, token: cancellationToken);


            foreach (Post post in wall.WallPosts)
            {
                if (post.Id == lastPostId)
                {
                    isLastPostEncountered = true;
                    break;
                }

                Log.Information($"Post: {post.Text}");
                newPosts.AddLast(post);
            }
        }

        if (newPosts.First is not null)
            AddOrUpdateGroupToWatch(groupId, newPosts.First.Value.Id);

        return newPosts;
    }

    private void AddOrUpdateGroupToWatch(long groupId, long? lastPostId)
    {
        _groupsToWatch.AddOrUpdate(groupId, _ => lastPostId, (_, _) => lastPostId);
    }
}