﻿using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using Newtonsoft.Json;

namespace VKConversationStylesGenerator {
    public class OutputStyles : ExecStylesResponse {
        [JsonProperty("names")]
        public Dictionary<string, List<StyleLang>> Names { get; set; }
    }

    public class Program {

        static string execBaseCode = @"
var forks = {
  '1': fork(API.messages.getAppearances()),
  '2': fork(API.messages.getBackgrounds()),
  '3': fork(API.messages.getConversationStyles())
};
var response = {
  'appearances': wait(forks['1']),
  'backgrounds': wait(forks['2']),
  'styles': wait(forks['3'])
};
response.appearances = response.appearances.appearances;
response.backgrounds = response.backgrounds.backgrounds;
response.styles = response.styles.items;
return response;
";

        static string execLangCode = @"
var forks = {
  '1': fork(API.messages.getConversationStylesLang({""lang"":""en""})),
  '2': fork(API.messages.getConversationStylesLang({""lang"":""ru""})),
  '3': fork(API.messages.getConversationStylesLang({""lang"":""uk""})),
  '4': fork(API.messages.getConversationStylesLang({""lang"":""az""})),
};
var r = {
  'lang_en': wait(forks['1']),
  'lang_ru': wait(forks['2']),
  'lang_uk': wait(forks['3']),
  'lang_az': wait(forks['4']),
};
return {
	'en': r.lang_en.styles,
	'ru': r.lang_ru.styles,
	'uk': r.lang_uk.styles,
	'az': r.lang_az.styles
};
";

        static string OutputPath = Environment.CurrentDirectory + Path.DirectorySeparatorChar;
        static string AccessToken;
        static bool RemoveUnnecessaryBackgrounds = false;

        static ExecStylesResponse StylesInfo;
        static Dictionary<string, List<StyleLang>> Names;

        static void Main(string[] args) {
            Console.WriteLine("VK CSG (Conversation Styles Generator) v1.0 by Elchin Orujov (ELOR).");
            if (args.Length == 0) {
                WriteInstruction();
            } else {
                foreach (string arg in args) {
                    if (arg.StartsWith("-t=")) {
                        AccessToken = arg.Substring(3);
                    } else if (arg.StartsWith("-o=")) {
                        string path = arg.Substring(3);
                        if (Path.IsPathFullyQualified(path)) OutputPath = path;
                    } else if (arg == "-b") {
                        RemoveUnnecessaryBackgrounds = true;
                    }
                }

                if (Path.EndsInDirectorySeparator(OutputPath)) OutputPath = Path.Combine(OutputPath, "chat_styles.json");
                Console.WriteLine($"Token is {AccessToken.Substring(0, 8)}********{AccessToken.Substring(AccessToken.Length - 8)}");
                Console.WriteLine($"Output file is {OutputPath}");
                Console.WriteLine($"Remove unnecessary backgrounds: {RemoveUnnecessaryBackgrounds}\n");

                Start().Wait();
                SecondPhase().Wait();
                ProcessAndSave();

                Console.WriteLine("All done!");
            }
        }

        private static async Task Start() {
            Console.Write($"Getting appearances, backgrounds and styles from VK... ");
            try {
                Dictionary<string, string> parameters = new Dictionary<string, string> {
                    { "code", execBaseCode }
                };
                StylesInfo = await CallAPIMehodAsync<ExecStylesResponse>("execute", parameters);
                Console.WriteLine($"OK!");
                Console.WriteLine($"Appearances: {StylesInfo.Appearances?.Count}, backgrounds: {StylesInfo.Backgrounds?.Count}; styles: {StylesInfo.Styles?.Count}.");
            } catch (Exception ex) {
                Console.WriteLine("FAILED!");
                WriteErrorAndQuit(ex);
            }
        }

        private static async Task SecondPhase() {
            Console.Write($"Getting localized names from VK... ");
            try {
                Dictionary<string, string> parameters = new Dictionary<string, string> {
                    { "code", execLangCode }
                };
                Names = await CallAPIMehodAsync<Dictionary<string, List<StyleLang>>>("execute", parameters);
                Console.WriteLine($"OK!");
            } catch (Exception ex) {
                Console.WriteLine("FAILED!");
                WriteErrorAndQuit(ex);
            }
        }

        private static void ProcessAndSave() {
            Console.Write($"Building and saving styles to file... ");
            try {
                // Removing unnecessary backgrounds
                if (RemoveUnnecessaryBackgrounds) {
                    List<Background> necessary = new List<Background>();
                    foreach (Style style in CollectionsMarshal.AsSpan(StylesInfo.Styles)) {
                        var background = StylesInfo.Backgrounds.Where(b => b.Id == style.BackgroundId).FirstOrDefault();
                        if (background != null) necessary.Add(background);
                    }
                    StylesInfo.Backgrounds = necessary;
                }

                var output = new OutputStyles {
                    Appearances = StylesInfo.Appearances,
                    Backgrounds = StylesInfo.Backgrounds,
                    Styles = StylesInfo.Styles,
                    Names = Names
                };

                string json = JsonConvert.SerializeObject(output);
                File.WriteAllText(OutputPath, json);

                Console.WriteLine($"OK!");
            } catch (Exception ex) {
                Console.WriteLine("FAILED!");
                WriteErrorAndQuit(ex);
            }
        }

        private static void WriteInstruction() {
            Console.WriteLine("Usage: vkcsg -t=ACCESS_TOKEN -o=OUTPUT -b");
            Console.WriteLine("-t (required) — access token from official VK app (android, ios or vk messenger);");
            Console.WriteLine("-o (optional) — output path. If a file with the same name exist, it will be overwritten;");
            Console.WriteLine("-b (optional) — don't add unnecessary backgrounds whose IDs are not in styles.");
        }

        private static void WriteErrorAndQuit(Exception ex) {
            if (ex is APIException apiex) {
                Console.WriteLine($"VK API returns an error {apiex.Code}!\n{apiex.Message}");
            } else {
                Console.WriteLine($"An error occured! HResult: 0x{ex.HResult.ToString("x8")}!\n{ex.Message}");
            }
            Process.GetCurrentProcess().Kill();
        }

        #region API requests


        static HttpClient httpClient = new HttpClient();

        private static async Task<T> CallAPIMehodAsync<T>(string method, Dictionary<string, string> parameters = null) {
            if (parameters == null) parameters = new Dictionary<string, string>();
            parameters.Add("v", "5.221");

            if (httpClient.DefaultRequestHeaders.Authorization == null)
                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
            HttpRequestMessage hmsg = new HttpRequestMessage(HttpMethod.Post, new Uri($"https://api.vk.com/method/{method}"));
            hmsg.Content = new FormUrlEncodedContent(parameters);

            var response = await httpClient.SendAsync(hmsg);
            response.EnsureSuccessStatusCode();
            string respString = await response.Content.ReadAsStringAsync();

            hmsg.Dispose();
            response.Dispose();

            VKAPIResponse<T> apiResponse = JsonConvert.DeserializeObject<VKAPIResponse<T>>(respString);
            if (apiResponse.Error != null) throw apiResponse.Error;
            if (apiResponse.ExecuteErrors != null) throw new APIExecuteException(apiResponse.ExecuteErrors);
            return apiResponse.Response;
        }

        #endregion
    }
}