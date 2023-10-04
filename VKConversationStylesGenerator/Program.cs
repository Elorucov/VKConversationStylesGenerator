using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using Newtonsoft.Json;

namespace VKConversationStylesGenerator {
    public class OutputStyles : ExecStylesResponse {
        [JsonProperty("creation_timestamp")]
        public long CreationTimestamp { get; set; }

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
var r = {
  'appearances': wait(forks['1']),
  'backgrounds': wait(forks['2']),
  'styles': wait(forks['3'])
};
return {
  'appearances': r.appearances.appearances,
  'backgrounds': r.backgrounds.backgrounds,
  'styles': r.styles.items,
};
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
        static bool DownloadBackgrounds = false;

        static ExecStylesResponse StylesInfo;
        static Dictionary<string, List<StyleLang>> Names;

        static void Main(string[] args) {
            var ver = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion;
            Console.WriteLine($"VK Conversation Styles Generator v{ver} by Elchin Orujov (ELOR)");
            foreach (string arg in args) {
                if (arg.StartsWith("-t=")) {
                    AccessToken = arg.Substring(3);
                } else if (arg.StartsWith("-o=")) {
                    string path = arg.Substring(3);
                    if (Path.IsPathFullyQualified(path)) OutputPath = path;
                } else if (arg == "-b") {
                    RemoveUnnecessaryBackgrounds = true;
                } else if (arg == "-d") {
                    DownloadBackgrounds = true;
                }
            }

#if DEBUG
            AccessToken = "YOUR_ACCESS_TOKEN_FOR_DEBUG_PURPOSES";
#endif

            if (String.IsNullOrWhiteSpace(AccessToken)) WriteInstructionAndQuit();
            if (Path.EndsInDirectorySeparator(OutputPath)) OutputPath = Path.Combine(OutputPath, "chat_styles.json");
            Console.WriteLine($"Output file is {OutputPath}");
            Console.WriteLine($"Remove unnecessary backgrounds: {RemoveUnnecessaryBackgrounds}\n");
            Console.WriteLine($"Download backgrounds: {DownloadBackgrounds}\n");

            Start().Wait();
            SecondPhase().Wait();
            ProcessAndSave();

            Console.WriteLine("All done!");
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

                // Download backgrounds
                if (DownloadBackgrounds) {
                    for (ushort i = 0; i < StylesInfo.Backgrounds.Count; i++) {
                        var background = StylesInfo.Backgrounds[i];
                        DownloadBackgroundAsync(background.Id + "_light", background.Light).Wait();
                        DownloadBackgroundAsync(background.Id + "_dark", background.Dark).Wait();
                        StylesInfo.Backgrounds[i] = background;
                    }
                }

                Console.Write($"Building and saving styles to file... ");
                var output = new OutputStyles {
                    CreationTimestamp = DateTimeOffset.Now.ToUnixTimeSeconds(),
                    Appearances = StylesInfo.Appearances,
                    Backgrounds = StylesInfo.Backgrounds,
                    Styles = StylesInfo.Styles,
                    Names = Names
                };

                string json = JsonConvert.SerializeObject(output, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
                File.WriteAllText(OutputPath, json);

                Console.WriteLine($"OK!");
            } catch (Exception ex) {
                Console.WriteLine("FAILED!");
                WriteErrorAndQuit(ex);
            }
        }

        private static async Task DownloadBackgroundAsync(string name, BackgroundSources background) {
            Console.Write($"Downloading background \"{name}\". Type: {background.Type}...");
            string link = String.Empty;

            if (background.Type == "vector" && background.Vector != null) {
                link = background.Vector.SVG?.Url;
            } else if (background.Type == "raster" && background.Raster != null) {
                link = background.Raster.Url;
            }
            if (String.IsNullOrEmpty(link)) {
                Console.WriteLine($"Background object or links not found!");
                return;
            }

            var split = OutputPath.Split(Path.DirectorySeparatorChar).ToList();
            split.Remove(split.Last());
            string outputFolder = String.Join(Path.DirectorySeparatorChar, split);
            string outputFileName = $"{name}{Path.GetExtension(link)}";

            var data = await httpClient.GetByteArrayAsync(link);
            File.WriteAllBytes(Path.Combine(outputFolder, outputFileName), data);

            if (background.Type == "vector" && background.Vector != null) {
                background.Vector.SVG.Url = outputFileName;
            } else if (background.Type == "raster" && background.Raster != null) {
                background.Raster.Url = outputFileName;
            }

            Console.WriteLine($"OK! (file name: {outputFileName})");
        }

        private static void WriteInstructionAndQuit() {
            Console.WriteLine("Usage: vkcsg -t=ACCESS_TOKEN -o=OUTPUT -b");
            Console.WriteLine("-t (required) — access token from official VK app (android, ios or vk messenger);");
            Console.WriteLine("-o (optional) — output path. If a file with the same name exist, it will be overwritten;");
            Console.WriteLine("-b (optional) — don't add unnecessary backgrounds whose IDs are not in styles;");
            Console.WriteLine("-d (optional) — download all backgrounds to output folder. Links of them in output file will be changed to local versions of backgrounds (example: \"url\":\"mable_light.svg\")");
            Process.GetCurrentProcess().Kill();
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