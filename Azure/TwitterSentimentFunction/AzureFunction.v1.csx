// For this azure function you can specify parameter in body like below and run azure function to get sentiment score.
// {
//   "hashTag": "ReleaseManagement",
//   "consumerKey": "z3MIAS6CFryAjes7jeu459oKKE",
//   "consumerSecret": "J0Xx4N0epU7woYJaLB5mlUYHPCxL22qEl0nbYcXlPTafUjfRBH",
//   "cognitiveServicesAccessKey": "2dbf45a2af524649a882e39c3138c156",
//   "cognitiveServicesEndpointRegion": "westus2",
//   "analyzeTweetsSince": "2018-01-01T07:56:59"
// }

#r "Newtonsoft.Json"
#r "System.Web"

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Web;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public static SentimentScore Run(TaskData req, TraceWriter log)
{
    log.Info("C# HTTP trigger function processed a request.");
    if (req == null)
    {
        return new SentimentScore("Invalid Input", -1, -1);
    }

    string hashTag = req.HashTag;
    string consumerKey = req.ConsumerKey;
    string consumerSecret = req.ConsumerSecret;
    string cognitiveServicesAccessKey = req.CognitiveServicesAccessKey;
    string cognitiveServicesEndpointRegion = req.CognitiveServicesEndpointRegion;
    DateTime analyzeTweetsSince = req.AnalyzeTweetsSince;
    if (string.IsNullOrWhiteSpace(hashTag) ||
        string.IsNullOrWhiteSpace(consumerKey) ||
        string.IsNullOrWhiteSpace(consumerSecret) ||
        string.IsNullOrWhiteSpace(cognitiveServicesAccessKey)||
        string.IsNullOrWhiteSpace(cognitiveServicesEndpointRegion))
    {
        return new SentimentScore("Invalid Input", -1, -1);
    }
	
    hashTag = hashTag.Trim();
    if (hashTag[0] != '#')
    {
		hashTag = string.Format(CultureInfo.InvariantCulture, "{0}{1}", "#", hashTag);
    }
			
    int max_tweets = 100;
    int tweetsCount = 0;
    double avgSentimentScore = 0.0;

    log.Info("C# HTTP trigger function getting tweets for hash " + hashTag);
    using (var httpClient = new HttpClient())
    {
        string tweetsData = SearchTweets(httpClient, consumerKey, consumerSecret, hashTag, max_tweets);
        if (tweetsData == null)
        {
            return new SentimentScore("Unable to retrieve tweets", -1, -1);
        }

        var tweets = JsonConvert.DeserializeObject(tweetsData);
        JObject batchInput = GetTweetsBatchInput(tweets, analyzeTweetsSince, out tweetsCount);
        if (tweetsCount == 0)
        {
            return new SentimentScore(hashTag, 0, 0);
        }

        log.Info("C# HTTP trigger function received tweets " + tweetsCount);
        var response = GetSentiment(httpClient, batchInput, cognitiveServicesAccessKey, cognitiveServicesEndpointRegion);
        if (response == null)
        {
            return new SentimentScore("Unable to find sentiment score for tweets", -1, -1);
        }
        log.Info("C# HTTP trigger function received sentiment score.");

        var tweetsSetimentData = JsonConvert.DeserializeObject(response);
        avgSentimentScore = CalculateSentimentScore(tweetsSetimentData, tweets);

        log.Info("C# HTTP trigger function processed sentiment score.");
    }

    return new SentimentScore(hashTag, avgSentimentScore, tweetsCount);
}

private static string SearchTweets(HttpClient httpClient, string consumerKey, string consumerSecret, string hashTag, int count)
{
    var request = new HttpRequestMessage(HttpMethod.Post, "https://api.twitter.com/oauth2/token");
    var customerInfo = Convert.ToBase64String(new UTF8Encoding().GetBytes(consumerKey + ":" + consumerSecret));
    request.Headers.Add("Authorization", "Basic " + customerInfo);
    request.Content = new StringContent("grant_type=client_credentials", Encoding.UTF8, "application/x-www-form-urlencoded");

    HttpResponseMessage response = httpClient.SendAsync(request).Result;

    if (response.StatusCode == HttpStatusCode.OK)
    {
        string json = response.Content.ReadAsStringAsync().Result;
        dynamic item = JsonConvert.DeserializeObject(json);
        string accessToken = item["access_token"];

        var requestUserTimeline = new HttpRequestMessage(HttpMethod.Get, string.Format("https://api.twitter.com/1.1/search/tweets.json?q={0}&count={1}", HttpUtility.UrlEncode(hashTag), count));
        requestUserTimeline.Headers.Add("Authorization", "Bearer " + accessToken);
        HttpResponseMessage responseUserTimeLine = httpClient.SendAsync(requestUserTimeline).Result;
        if (responseUserTimeLine.StatusCode == HttpStatusCode.OK)
        {
            return responseUserTimeLine.Content.ReadAsStringAsync().Result;
        }
    }

    return null;
}

private static JObject GetTweetsBatchInput(dynamic tweets, DateTime since, out int tweetCount)
{
    string twitterTimeformat = "ddd MMM dd HH:mm:ss zzz yyyy";
    int i = 0;
    List<JObject> tweetsData = new List<JObject>();
    foreach (var t in tweets["statuses"])
    {
        DateTime tweetCreatedOn = DateTime.ParseExact((string)t["created_at"], twitterTimeformat, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        if (DateTime.Compare(tweetCreatedOn, since) > 0)
        {
            tweetsData.Add(new JObject(new JProperty("language", t["lang"]), new JProperty("id", i++), new JProperty("text", t["text"])));
        }
    }

    tweetCount = tweetsData.Count;
    return new JObject(new JProperty("documents", tweetsData));
}

private static string GetSentiment(HttpClient httpClient, JObject tweetsData, string cognitiveServicesAccessKey, string cognitiveServicesEndpointRegion)
{
    string requestBody = JsonConvert.SerializeObject(tweetsData);
    string url = string.Format("https://{0}.api.cognitive.microsoft.com/text/analytics/v2.0/sentiment", cognitiveServicesEndpointRegion);
    string resultContent = string.Empty;

    StringContent queryString = new StringContent(requestBody);
    httpClient.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", cognitiveServicesAccessKey);
    var buffer = System.Text.Encoding.UTF8.GetBytes(requestBody);
    var byteContent = new ByteArrayContent(buffer);
    byteContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");
    HttpResponseMessage response = httpClient.PostAsync(new Uri(url), byteContent).Result;
    if (response.StatusCode == HttpStatusCode.OK)
    {
        return response.Content.ReadAsStringAsync().Result;
    }

    return null;
}
    
private static double CalculateSentimentScore(dynamic tweetsSentimentData, dynamic tweetsData)
{
    double score = 0.0;
    int count = 0;
    int retweet_count = 0;
    int favorite_count = 0;
    int retweet_favorite_count = 0;
    foreach (var t in tweetsSentimentData["documents"])
    {
        var tId = tweetsData["statuses"][(int)t["id"]];
        retweet_count = tId["retweet_count"];
        favorite_count = tId["favorite_count"];
        retweet_favorite_count = (retweet_count + favorite_count) < 2 ? 1 : (retweet_count + favorite_count);
        score += retweet_favorite_count * (double)t["score"];
        count += retweet_favorite_count;
    }

    return count == 0 ? 0 : score / count;
}

public class TaskData
{
    public string HashTag { get; set; }
    public string ConsumerKey { get; set; }
    public string ConsumerSecret { get; set; }
    public string CognitiveServicesAccessKey { get; set; }
    public string CognitiveServicesEndpointRegion { get; set; }
    public DateTime AnalyzeTweetsSince { get; set; }
}

public class SentimentScore
{
    public SentimentScore(string hashTag, double score, int count)
    {
        HashTag = hashTag;
        Score = score;
        NumberOfTweetsParsed = count;
    }

    public string HashTag { get; set; }
    public double Score { get; set; }
    public int NumberOfTweetsParsed { get; set; }
}