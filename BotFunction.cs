using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Host;
using MongoDB.Driver;
using Newtonsoft.Json;
using Telegram.Bot.Types;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

public static class BotFunction
{
    private static readonly string connectionUri = Environment.GetEnvironmentVariable("MONGO_CONNECTION_URI");
    private static readonly string dbName = Environment.GetEnvironmentVariable("DATABASE_NAME");
    private static readonly MongoClient dbClient = new MongoClient(connectionUri);
    private static readonly IMongoDatabase db = dbClient != null ? dbClient.GetDatabase(dbName) : null;
    private static readonly TelegramBotClient botClient = new TelegramBotClient(Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN"));
    private static readonly IMongoCollection<User> usersCollection = db != null ? db.GetCollection<User>("users") : null;
    private static Dictionary<long, AddCommandState> userStates = new Dictionary<long, AddCommandState>();
    private static Dictionary<long, bool> deleteStates = new Dictionary<long, bool>();

    [FunctionName("BotFunction")]

    public static async Task<IActionResult> Update(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = null)] HttpRequest req,
            TraceWriter log)
    {
        log.Info("BotFunction is processing a request.");

        string jsonContent = await req.ReadAsStringAsync();
        var update = JsonConvert.DeserializeObject<Update>(jsonContent);

        if (update == null || update.Message == null || update.Message.From == null || string.IsNullOrEmpty(update.Message.Text))
        {
            return new OkResult();
        }

        var id = update.Message.Chat.Id;
        var type = update.Message.Chat.Type == ChatType.Private ? "user" : "group";

        if (update.Type == UpdateType.Message)
        {
            if (update.Message.Text.StartsWith("/start"))
            {
                await HandleStartCommand(update.Message, type);
            }
            else if ((update.Message.Text.StartsWith("/add") || userStates.ContainsKey(id)))
            {
                await HandleAddCommand(update.Message, botClient, id, type);
            }
            else if (update.Message.Text.StartsWith("/ping"))
            {
                await HandlePingCommand(update.Message, botClient);
            }
            else if (update.Message.Text.StartsWith("/list"))
            {
                await HandleListCommand(update.Message, botClient);
            }
            else if (update.Message.Text.StartsWith("/delete"))
            {
                await HandleDeleteCommand(update.Message, botClient);
            }
            else if (deleteStates.ContainsKey(id) && deleteStates[id])
            {
                await HandleDeleteResponse(update.Message, botClient);
            }
        }

        return new OkResult();
    }


    public class User
    {
        [BsonId]
        public long Id { get; set; }
        public string Type { get; set; }
        public List<Event> Events { get; set; }
    }

    public class Event
    {
        public DateTime DateTime { get; set; }
        public string Description { get; set; }
    }
    private enum AddCommandStep
    {
        AwaitingDate,
        AwaitingTime,
        AwaitingDescription
    }
    private class AddCommandState
    {
        public AddCommandStep Step { get; set; }
        public DateTime Date { get; set; }
        public TimeSpan Time { get; set; }
        public string Description { get; set; }
    }

    private static async Task HandleStartCommand(Message message, string type)
    {
        var userCollection = db.GetCollection<User>("users");
        var id = message.Chat.Id;
        var userFilter = Builders<User>.Filter.Eq(u => u.Id, id);
        var userExists = (await userCollection.CountDocumentsAsync(userFilter)) > 0;

        if (userExists)
        {
            await botClient.SendTextMessageAsync(message.Chat.Id, "Hello, welcome back!");
            await botClient.DeleteMessageAsync(message.Chat.Id, message.MessageId);
        }
        else
        {
            User newUser = new User { Id = id, Type = type, Events = new List<Event>() };
            await userCollection.InsertOneAsync(newUser);
            await botClient.SendTextMessageAsync(message.Chat.Id, "Hello, welcome to our bot!");
        }
    }


    private static async Task HandleAddCommand(Message message, ITelegramBotClient botClient, long id, string type)
    {
        Console.WriteLine($"Handling add command for user {id} with message {message.Text}");

        if (message.Text == "/add" || message.Text == "/add@malds_scheduler_bot")
        {
            userStates[id] = new AddCommandState { Step = AddCommandStep.AwaitingDate };
            Console.WriteLine($"Added state for user {id} to userStates");
            await botClient.SendTextMessageAsync(message.Chat.Id, "Enter date of event (format dd-mm-yyyy or dd-mm or dd. You can use - , . or space as a separator)");
        }
        else if (userStates.ContainsKey(id))
        {
            var state = userStates[id];
            switch (state.Step)
            {
                case AddCommandStep.AwaitingDate:
                    Console.WriteLine($"Received input in AwaitingDate step: {message.Text}");
                    string[] dateParts = message.Text.Split(new[] { '-', ',', '.', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (dateParts.Length == 0 || dateParts.Length > 3)
                    {
                        await botClient.SendTextMessageAsync(message.Chat.Id, "Invalid date format. Please enter the date as dd-mm-yyyy, dd-mm or dd.");
                        break;
                    }
                    int day = int.Parse(dateParts[0]);
                    int month = (dateParts.Length >= 2) ? int.Parse(dateParts[1]) : DateTime.Now.Month;
                    int year = (dateParts.Length == 3) ? int.Parse(dateParts[2]) : DateTime.Now.Year;
                    if (day < 1 || day > 31 || month < 1 || month > 12 || year < 0)
                    {
                        await botClient.SendTextMessageAsync(message.Chat.Id, "Invalid date. Please check your input and try again.");
                        break;
                    }
                    state.Date = new DateTime(year, month, day);
                    state.Step = AddCommandStep.AwaitingTime;
                    Console.WriteLine($"Moving to AwaitingTime step");
                    await botClient.SendTextMessageAsync(message.Chat.Id, "Enter time of event (format hh:mm)");
                    break;

                case AddCommandStep.AwaitingTime:
                    string inputTime = message.Text;
                    int hours, minutes;
                    if (inputTime.Contains(".") || inputTime.Contains(":") || inputTime.Contains("-"))
                    {
                        string[] timeParts = inputTime.Split(new[] { '-', ',', '.', ':' }, StringSplitOptions.RemoveEmptyEntries);
                        hours = int.Parse(timeParts[0]);
                        minutes = int.Parse(timeParts[1]);
                    }
                    else
                    {
                        if (inputTime.Length == 3)
                        {
                            hours = int.Parse(inputTime.Substring(0, 1));
                            minutes = int.Parse(inputTime.Substring(1, 2));
                        }
                        else if (inputTime.Length == 4)
                        {
                            hours = int.Parse(inputTime.Substring(0, 2));
                            minutes = int.Parse(inputTime.Substring(2, 2));
                        }
                        else
                        {
                            await botClient.SendTextMessageAsync(message.Chat.Id, "Invalid time format. Please enter the time as hh:mm, hhmm, or h:mm.");
                            break;
                        }
                    }
                    if (hours < 0 || hours > 23 || minutes < 0 || minutes > 59)
                    {
                        await botClient.SendTextMessageAsync(message.Chat.Id, "Invalid time. Please check your input and try again.");
                        break;
                    }
                    state.Time = new TimeSpan(hours, minutes, 0);
                    state.Step = AddCommandStep.AwaitingDescription;
                    await botClient.SendTextMessageAsync(message.Chat.Id, "Enter description of event");
                    break;

                case AddCommandStep.AwaitingDescription:
                    state.Description = message.Text;
                    DateTime eventDateTime = state.Date.Add(state.Time);
                    Event newEvent = new Event
                    {
                        DateTime = eventDateTime,
                        Description = state.Description
                    };

                    var userCollection = db.GetCollection<User>("users");
                    var userFilter = Builders<User>.Filter.Eq(u => u.Id, id);
                    var user = await userCollection.Find(userFilter).SingleOrDefaultAsync();

                    if (user == null)
                    {
                        Console.WriteLine("user == null");
                    }
                    else
                    {
                        if (user.Events == null)
                        {
                            user.Events = new List<Event>();
                        }
                        user.Events.Add(newEvent);
                        var userUpdate = Builders<User>.Update.Set(u => u.Events, user.Events);
                        await userCollection.UpdateOneAsync(userFilter, userUpdate);
                    }
                    userStates.Remove(id);
                    await botClient.SendTextMessageAsync(message.Chat.Id, "Succesfully added new event!");
                    Console.WriteLine($"Removing state for user {id} from userStates");
                    break;
            }
        }
    }

    private static async Task HandlePingCommand(Message message, ITelegramBotClient botClient)
    {
        var settings = MongoClientSettings.FromConnectionString(connectionUri);
        settings.ServerApi = new ServerApi(ServerApiVersion.V1);
        var client = new MongoClient(settings);
        try
        {
            var result = client.GetDatabase("admin").RunCommand<BsonDocument>(new BsonDocument("ping", 1));
            await botClient.SendTextMessageAsync(message.Chat.Id, "Don't worry! Everything is ok!");
            await botClient.DeleteMessageAsync(message.Chat.Id, message.MessageId);
        }
        catch (Exception ex)
        {
            await botClient.SendTextMessageAsync(message.Chat.Id, ex.Message);
        }
    }

    private static async Task HandleListCommand(Message message, ITelegramBotClient botClient)
    {
        var id = message.Chat.Id;
        var userFilter = Builders<User>.Filter.Eq(u => u.Id, id);
        var user = await usersCollection.Find(userFilter).SingleOrDefaultAsync();

        if (user == null || user.Events == null || user.Events.Count == 0)
        {
            await botClient.SendTextMessageAsync(message.Chat.Id, "You have no events!");
            await botClient.DeleteMessageAsync(message.Chat.Id, message.MessageId);
        }
        else
        {
            var sortedEvents = user.Events.OrderBy(e => e.DateTime).ToList();

            string listMessage = "Here are your events:\n";
            foreach (var eventItem in sortedEvents)
            {
                listMessage += $"🔘 {eventItem.DateTime.ToString("dd-MM-yy HH:mm")}: {eventItem.Description}\n";
            }
            await botClient.SendTextMessageAsync(message.Chat.Id, listMessage);
            await botClient.DeleteMessageAsync(message.Chat.Id, message.MessageId);
        }
    }
    private static async Task HandleDeleteCommand(Message message, ITelegramBotClient botClient)
    {
        var id = message.Chat.Id;
        var userFilter = Builders<User>.Filter.Eq(u => u.Id, id);
        var user = await usersCollection.Find(userFilter).SingleOrDefaultAsync();

        if (user == null || user.Events == null || user.Events.Count == 0)
        {
            await botClient.SendTextMessageAsync(message.Chat.Id, "You have no events!");
        }
        else
        {
            var sortedEvents = user.Events.OrderBy(e => e.DateTime).ToList();

            string listMessage = "Here are your events:\n";
            for (int i = 0; i < sortedEvents.Count; i++)
            {
                listMessage += $"{i + 1}. {sortedEvents[i].DateTime.ToString("dd-MM-yy HH:mm")}: {sortedEvents[i].Description}\n";
            }
            listMessage += "\nReply with the number(s) of the event(s) you want to delete. You can enter multiple numbers or ranges (e.g. '2, 4-7, 9').";
            await botClient.SendTextMessageAsync(message.Chat.Id, listMessage);
            deleteStates[id] = true;
        }
    }

    private static async Task HandleDeleteResponse(Message message, ITelegramBotClient botClient)
    {
        var id = message.Chat.Id;
        var userFilter = Builders<User>.Filter.Eq(u => u.Id, id);
        var user = await usersCollection.Find(userFilter).SingleOrDefaultAsync();
        var numbers = new List<int>();
        var parts = message.Text.Split(new[] { ',', ' ', '.' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            if (part.Contains("-"))
            {
                var rangeParts = part.Split('-');
                if (rangeParts.Length != 2)
                {
                    await botClient.SendTextMessageAsync(message.Chat.Id, $"Invalid range: '{part}'. Please try again.");
                    return;
                }
                int start = int.Parse(rangeParts[0]);
                int end = int.Parse(rangeParts[1]);
                numbers.AddRange(Enumerable.Range(start, end - start + 1));
            }
            else
            {
                numbers.Add(int.Parse(part));
            }
        }

        numbers = numbers.Distinct().OrderByDescending(n => n).ToList();

        foreach (var number in numbers)
        {
            if (number < 1 || number > user.Events.Count)
            {
                await botClient.SendTextMessageAsync(message.Chat.Id, $"Invalid number: {number}. Please try again.");
                return;
            }
            user.Events.RemoveAt(number - 1);
        }

        var userUpdate = Builders<User>.Update.Set(u => u.Events, user.Events);
        await usersCollection.UpdateOneAsync(userFilter, userUpdate);

        await botClient.SendTextMessageAsync(message.Chat.Id, "Event(s) deleted!");

        deleteStates[id] = false;
    }
}