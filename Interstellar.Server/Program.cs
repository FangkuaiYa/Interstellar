namespace Interstellar.Server
{
    internal class Program
    {
        static void Main(string[] args)
        {
            string urlPrefix = "http://";
            string? certPath = null;
            string? password = null;
            bool secure = false;
            string? turnUrl = null;
            string? turnUser = null;
            string? turnPass = null;

            if(args.Length > 1)
            {
                for(int i = 1; i < args.Length; i++)
                {
                    bool isTerminal = i + 1 == args.Length;
                    switch (args[i])
                    {
                        case "-secure":
                        case "-s":
                            secure = true;
                            urlPrefix = "https://";
                            if (!isTerminal)
                            {
                                certPath = args[i + 1];
                                i++;
                            }
                            break;
                        case "-password":
                        case "-p":
                            if (!isTerminal)
                            {
                                password = args[i + 1];
                                i++;
                            }
                            break;
                        case "--coturn":
                        case "-t":
                            if (!isTerminal)
                            {
                                turnUrl = args[i + 1];
                                i++;
                            }
                            break;
                        case "--coturn-user":
                            if (!isTerminal)
                            {
                                turnUser = args[i + 1];
                                i++;
                            }
                            break;
                        case "--coturn-pass":
                            if (!isTerminal)
                            {
                                turnPass = args[i + 1];
                                i++;
                            }
                            break;
                    }
                }
            }

            string url = urlPrefix + "localhost:8000";
            if (args.Length >= 1 && !args[0].StartsWith("-")) url = urlPrefix + args[0];

            Console.WriteLine("Starting Interstellar Voice Server at " + url);
            if (!string.IsNullOrEmpty(turnUrl))
                Console.WriteLine("  Coturn TURN server: " + turnUrl);

            Server.StartServer(url, secure, certPath, password, turnUrl, turnUser, turnPass);
        }
    }
}
