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
            int optimalPlayers = 0;

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
                        case "--optimal-players":
                        case "-op":
                            if (!isTerminal && int.TryParse(args[i + 1], out var op))
                            {
                                optimalPlayers = op;
                                i++;
                            }
                            break;
                    }
                }
            }

            string url = urlPrefix + "localhost:8000";
            if (args.Length >= 1 && !args[0].StartsWith("-")) url = urlPrefix + args[0];

            // Fall back to OPTIMAL_PLAYERS env var when not set via CLI (e.g. in Docker)
            if (optimalPlayers <= 0)
            {
                var envOptimal = System.Environment.GetEnvironmentVariable("OPTIMAL_PLAYERS");
                if (!string.IsNullOrEmpty(envOptimal) && int.TryParse(envOptimal, out var envOp))
                    optimalPlayers = envOp;
            }

            Console.WriteLine("Starting Interstellar Voice Server at " + url);
            if (optimalPlayers > 0)
                Console.WriteLine("  Optimal players: " + optimalPlayers);
            if (!string.IsNullOrEmpty(turnUrl))
                Console.WriteLine("  Coturn TURN server: " + turnUrl);

            Server.StartServer(url, secure, certPath, password, turnUrl, turnUser, turnPass, optimalPlayers);
        }
    }
}
