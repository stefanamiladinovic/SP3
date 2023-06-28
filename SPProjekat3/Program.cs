using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Net.Http;
using Newtonsoft.Json;
using System.Net;
using System.Reactive.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Subjects;
using System.Net.Http.Headers;
using Reddit;
using Reddit.Controllers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using VaderSharp;
using Reddit.Inputs;

string listeningPort = "http://localhost:5050/";

// for authorization and access token
string baseEndpoint = "https://www.reddit.com/api/v1/";
string authorizationEndpoint = baseEndpoint + "authorize";
string tokenEndpoint = baseEndpoint + "access_token";
string scope = "read identity submit";
string userAgent = "This is a system programming app used for fetching all comments for subreddits";
string redirectUri = listeningPort;

// for access token
string clientId = "UrAT6ur2fuEOpaXyimMePA"; 
string clientSecret = "JPmoIXPn_oCXasjXwp0j5UAV6SN5PQ";
RedditToken token = new RedditToken();
DateTime? expirationTime = null;


// shared resources
int numberOfRequest = 0;
object consoleLocker = new object();
SemaphoreSlim semaphoreSlim = new SemaphoreSlim(1, 1);

StartServer();

 void StartServer()
 {
    Thread serverThread = new Thread(() =>
    {
        HttpListener listener = new HttpListener();
        listener.Prefixes.Add(listeningPort);
        listener.Start();
        Console.WriteLine($"Server is listening on {listener.Prefixes.Last()}");

        while (true)
        {
             var context = listener.GetContext();

            Task task = Task.Run(async () =>
            {
                var request = context.Request;
                var response = context.Response;
                Response res = new Response();
                int myRequestNumber = 0;

                lock (consoleLocker)
                {
                    myRequestNumber = ++numberOfRequest;
                    LogRequest(request, myRequestNumber);
                }

                if (request.HttpMethod != "GET")
                {
                    res.text = "This is not a valid request! Only GET methods are allowed";
                    res.byteBuffer = System.Text.Encoding.UTF8.GetBytes(res.text);
                }
                else
                {
                    res = new Response();
                    List<string> subrredits = ParseQueryString(request);
                    
                    if (subrredits != null)

                    {    //getting access token
                        if (expirationTime == null || DateTime.Now.Subtract(expirationTime.Value).Seconds >= 77760)
                        {
                            await semaphoreSlim.WaitAsync();
                            if (expirationTime == null || DateTime.Now.Subtract(expirationTime.Value).Seconds >= 77760)
                                await GetAccessTokenAsync();
                            semaphoreSlim.Release();
                        }

                        // creating subreddits
                        MySubreddit[] subreds = new MySubreddit[subrredits.Count];
                        for (int i = 0; i < subrredits.Count; i++)
                        {
                         var reddit = new RedditClient(appId: clientId, refreshToken: token.refresh_token, accessToken: token.access_token);
                         subreds[i] = new MySubreddit(reddit, subrredits[i],response);
                        }

                        // fetching posts for subreddits
                        var mergedObservable = Observable.Merge(subreds);
                        var subscription = mergedObservable.Subscribe(res);
                        IDisposable[] subscriptions = new IDisposable[subreds.Length];

                        int j = 0;
                        foreach (var sub in subreds)
                        {
                            Console.WriteLine("Fetching posts for subreddit: " + sub.Name);
                            var stream = new PostStream(userAgent, token.access_token);
                            subscriptions[j++] = stream.Subscribe(sub);
                            await stream.GetSubredditPosts(sub.Name);
                        }

                        while (res.IsCompleted == false) { }

                        subscription.Dispose();
                        foreach (var sub in subscriptions) 
                        {
                             sub.Dispose();
                        }
                    }
                    else
                    {
                        res.text = "This is not a valid request! subreddit parameter is missing";
                        res.byteBuffer = System.Text.Encoding.UTF8.GetBytes(res.text);
                    }
                }

                await SendResponseAsync(response, res);

                lock (consoleLocker)
                {
                    LogResponse(response, myRequestNumber);
                }
            });
        }

        listener.Close();
    });

        serverThread.Start();
        serverThread.Join();
}



async Task GetAccessTokenAsync()
{
    var client = new HttpClient();

    var tokenRequestBody = new Dictionary<string, string>
    {
        { "grant_type", "client_credentials" },
        { "scope", scope }
    };

    client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
    "Basic", Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($"{clientId}:{clientSecret}")));
    client.DefaultRequestHeaders.Add("User-Agent", $"{userAgent}");

    var tokenResponse = await client.PostAsync(tokenEndpoint, new FormUrlEncodedContent(tokenRequestBody));
    var responseContent = await tokenResponse.Content.ReadAsStringAsync();

    client.Dispose();

    token = JsonConvert.DeserializeObject<RedditToken>(responseContent);
    expirationTime = DateTime.Now;

    lock (consoleLocker)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("Reddit token refreshed");
        Console.ResetColor();
    }
}

void LogRequest(HttpListenerRequest request, int requestNumber)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"Logging request {requestNumber}");
    Console.ResetColor();
    Console.WriteLine(request.HttpMethod);
    Console.WriteLine(request.ProtocolVersion);
    Console.WriteLine(request.Url);
    Console.WriteLine(request.RawUrl);
    Console.WriteLine(request.Headers);
}

void LogResponse(HttpListenerResponse response, int requestNumber)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"Logging response for request {requestNumber}");
    Console.ResetColor();
    Console.WriteLine(response.StatusCode);
    Console.WriteLine(response.StatusDescription);
    Console.WriteLine(response.ProtocolVersion);
    Console.WriteLine(response.Headers);
}

List<string> ParseQueryString(HttpListenerRequest request)
{
    List<string> subreddits = new List<string>();
    bool subredditFound = false;

    for (int i = 0; i < request.QueryString.Count; i++)
    {
        var key = request.QueryString.GetKey(i);
        var values = request.QueryString.GetValues(key);
        Console.WriteLine(key + "                      " + values);
        if (key == "subreddit")
        {
            if (values.Length > 0)
            {
                foreach (var val in values)
                {
                    subreddits.Add(Uri.EscapeDataString(val));
                }
            }
            subredditFound = true;
        }
    }
    if (subredditFound)
    return subreddits;
    else
    return null;
}

async Task SendResponseAsync(HttpListenerResponse response, Response res)
    {
        response.ContentLength64 = res.byteBuffer.Length;
        var output = response.OutputStream;
        await output.WriteAsync(res.byteBuffer, 0, res.byteBuffer.Length);
        output.Close();
    }
class Response : IObserver<string>
{
    public string text = "";
    public byte[] byteBuffer;
    public bool IsCompleted { get; private set; }

    public void OnNext(string value)
    {
        text = text + value + "\n";
    }
    public void OnCompleted()
    {
        if (this.text.Length == 0)
        {
            this.text = " ";//"No locations found!";
        }
        this.byteBuffer = System.Text.Encoding.UTF8.GetBytes(this.text);
        IsCompleted = true;
    }
    public void OnError(Exception error)
    {
        text = text + error.Message + "\n";
        this.byteBuffer = System.Text.Encoding.UTF8.GetBytes(this.text);
        IsCompleted = true;
    }
}
class RedditToken
{
    public string access_token { get; set; }
    public string token_type { get; set; }
    public int expires_in { get; set; }
    public string refresh_token { get; set; }
    public string scope { get; set; }
}



internal class PostStream : IObservable<string>
{
    // reactive programming
    private readonly IScheduler scheduler;
    //used to publish and subscribe to events
    private ISubject<string> postSubject;

    public string UserAgent { get; set; }
    public string Token { get; set; }

    public PostStream(string userAgent, string token)
    {
        postSubject = new Subject<string>();
        scheduler = new EventLoopScheduler();
        UserAgent = userAgent;
        Token = token;
    }
   
    public async Task GetSubredditPosts(string subredditName)
    {
        string responseBody;
        List<string> posts = new List<string>();
        string url = $"https://oauth.reddit.com/r/{subredditName}/new.json";

        HttpClient client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        client.DefaultRequestHeaders.Add("User-Agent", $"{UserAgent}");

        try
        {
            var response = await client.GetAsync(url);
            responseBody = await response.Content.ReadAsStringAsync();

            client.Dispose();

            // extracting post id's

            JObject responseJson = JObject.Parse(responseBody);
            JArray children = (JArray)responseJson["data"]!["children"]!;

            foreach (JToken child in children)
            {
                JObject postData = (JObject)child["data"]!;
                string name = (string)postData["name"]!;

                if (name != "")
                {
                    postSubject.OnNext(name!);
                }
            }
            postSubject.OnCompleted();
        }
        catch (Exception ex)
        {
            postSubject.OnError(ex);
        }
    }

    public IDisposable Subscribe(IObserver<string> observer)
    {
        return postSubject.ObserveOn(scheduler).Subscribe(observer);
        //return postSubject.Subscribe(observer);
    }

}


 internal class MySubreddit : IObserver<string>, IObservable<string>
{
    // reactive programming
    private ISubject<string> resultsSubject;
    private readonly IScheduler scheduler;
    
    // locks
    private object locker;
    private static object consolelocker;

    // tasks
    public ConcurrentBag<Task> Tasks;

    // subreddit
    public string Name { get; set; }
    public RedditClient Reddit { get; set; }

    // comments
    public int NumComments { get; set; }
    public ConcurrentBag<string> CommentText { get; set; }
    public ConcurrentBag<string> CommentIds { get; set; }

    HttpListenerResponse response;
    public MySubreddit(RedditClient redit, string name,HttpListenerResponse response)
    {
        Reddit = redit;
        Name = name;
        NumComments = 0;
        locker = new object();
        CommentIds = new ConcurrentBag<string>();
        CommentText = new ConcurrentBag<string>();
        Tasks = new ConcurrentBag<Task>();
        resultsSubject = new Subject<string>();
        consolelocker = new object();
        scheduler = new EventLoopScheduler();
        this.response = response;
    }

    private void IterateComments(IList<Comment> comments, Post post, int depth = 0)
    {
        foreach (Comment comment in comments)
        {
            AddComment(comment, depth);
            IterateComments(comment.Replies, post, (depth + 1));
            IterateComments(GetMoreChildren(comment, post), post, depth);
        }
    }

    private IList<Comment> GetMoreChildren(Comment comment, Post post)
    {
        List<Comment> res = new List<Comment>();
        if (comment.More == null)
        {
            return res;
        }

        foreach (Reddit.Things.More more in comment.More)
        {
            foreach (string id in more.Children)
            {
                if (!CommentIds.Contains(id))
                {
                    res.Add(post.Comment("t1_" + id).About());
                }
            }
        }
        return res;
    }
   
    private void AddComment(Comment comment, int depth = 0)
    {
        if (comment == null || string.IsNullOrWhiteSpace(comment.Author))
        {
            return;
        }

        lock (locker)
        {
            NumComments++;
        }

        if (!CommentIds.Contains(comment.Id))
        {
            CommentIds.Add(comment.Id);
        }

        CommentText.Add(comment.Body);
    }

    async Task SendResponseAsync(HttpListenerResponse response, Response res)
    {
        response.ContentLength64 = res.byteBuffer.Length;
        var output = response.OutputStream;
        await output.WriteAsync(res.byteBuffer, 0, res.byteBuffer.Length);
        output.Close();
    }
    private async void AnalyzeComments()
    {


        //VaderSharp
        var analyzer = new SentimentIntensityAnalyzer();
        int numPositive = 0;
        int numNegative = 0;
        int numNeutral = 0;
        foreach (string comment in CommentText)
        {


            // Loop through each comment and analyze its sentiment

            var results = analyzer.PolarityScores(comment);

            // Determine the overall sentiment of the comment
            if (results.Compound > 0.05)
            {
                numPositive++;
            }
            else if (results.Compound < -0.05)
            {
                numNegative++;
            }
            else
            {
                numNeutral++;
            }
        }

        // Calculate the percentages of positive, negative, and neutral comments
        double percentPositive = ((double)numPositive / NumComments) * 100;
        double percentNegative = ((double)numNegative / NumComments) * 100;
        double percentNeutral = ((double)numNeutral / NumComments) * 100;

        // Print out the results
        Console.WriteLine("Sentiment analysis results:");
        Console.WriteLine("------------------------------");
        Console.WriteLine($"Positive: {percentPositive}%");
        Console.WriteLine($"Negative: {percentNegative}%");
        Console.WriteLine($"Neutral: {percentNeutral}%");
        Response res = new Response();
        res.text = "Sentiment analysis results:\n"+ $"Positive: {percentPositive}%\n"+ $"Negative: {percentNegative}%\n"+ $"Neutral: {percentNeutral}%\n";
        res.byteBuffer = System.Text.Encoding.UTF8.GetBytes(res.text);
        await SendResponseAsync(response, res);
        resultsSubject.OnCompleted();

    }

    private async Task<bool> RunTask(string postId)
    {
        try
        {
           

            Post post = Reddit.Subreddit(Name).Post(postId).About();

            lock (consolelocker)
            {
                Console.WriteLine(post.Title);
                Console.WriteLine("There are " + post.Listing.NumComments + " comments");
            }

            IterateComments(post.Comments.GetNew(limit: 500), post);
            return true;
        }
        catch (Exception ex)
        {
            lock (consolelocker)
            {
                Console.ForegroundColor = ConsoleColor.Magenta;
                Console.WriteLine(ex.Message);
                Console.ForegroundColor = ConsoleColor.White;
            }
            return false;
        }
    }
    public async void OnNext(string postId)
    {
        int currentTry = 0;
        int maxTries = 3;
        bool success = false;

        while (!success && currentTry < maxTries)
        {
            var task = RunTask(postId);
            Tasks.Add(task);
            success = await task;

            if (currentTry > 0)
                lock (consolelocker)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("From Attempt " + currentTry);
                    Console.ForegroundColor = ConsoleColor.White;
                }

            if (!success)
            {
                currentTry++;

                lock (consolelocker)
                {
                    Console.ForegroundColor = ConsoleColor.Magenta;
                    Console.WriteLine("Attempt number " + currentTry);
                    Console.ForegroundColor = ConsoleColor.White;
                }
            }
        }
    }

    public void OnCompleted()
    {
        Task.WaitAll(Tasks.ToArray());

        lock (consolelocker)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"\nSubreddit {Name}\nTotal comments to analyze: {NumComments}\n");
            Console.ForegroundColor = ConsoleColor.White;
        }

        AnalyzeComments();
    }

    public void OnError(Exception error)
    {
        resultsSubject.OnError(error);
    }

    public IDisposable Subscribe(IObserver<string> observer)
    {
        //return resultsSubject.Subscribe(observer);
        return resultsSubject.ObserveOn(scheduler).Subscribe(observer);
    }
}