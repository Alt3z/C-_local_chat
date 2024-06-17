using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System;
using System.IO;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace ChatClient 
{
    public partial class MainWindow : Window 
    {
        private TcpClient client; 
        private NetworkStream stream; 
        private const int broadcastPort = 8888; 
        private const string broadcastRequest = "DISCOVER_CHAT_SERVER"; 
        private const string broadcastResponse = "CHAT_SERVER_RESPONSE"; 
        private string serverIp; 
        private const string fileSavePath = @"/home/{username}/Рабочий стол/File"; 

        public MainWindow()
        {
            InitializeComponent();
        }

        private async void OnConnectButtonClicked(object sender, RoutedEventArgs e)
        {
            string userName = UserNameBox.Text;
            if (string.IsNullOrWhiteSpace(userName))
            {
                ChatBox.Text += "Введите корректное имя.\n";
                return;
            }

            try
            {
                serverIp = await DiscoverServerIpAsync(); // Широковещательный запрос
                if (serverIp == null) 
                {
                    ChatBox.Text += "Сервер не найден.\n"; 
                    return;
                }

                client = new TcpClient();
                await client.ConnectAsync(serverIp, broadcastPort); 
                stream = client.GetStream(); 

                byte[] nameBuffer = Encoding.UTF8.GetBytes(userName); 
                await stream.WriteAsync(nameBuffer, 0, nameBuffer.Length); // Отправка имени пользователя на сервер

                Task.Run(() => ReceiveMessages());

                MessageBox.IsEnabled = true; 
                SendButton.IsEnabled = true; 
                SendFileButton.IsEnabled = true; 
                ConnectButton.IsEnabled = false; 
                UserNameBox.IsEnabled = false; 

                ChatBox.Text += "Подключен к серверу.\n";
            }
            catch (Exception ex)
            {
                ChatBox.Text += $"Ошибка подключения к серверу: {ex.Message}\n";
            }
        }

        private async Task<string> DiscoverServerIpAsync() // Метод для широковещательного запроса
        {
            using (UdpClient udpClient = new UdpClient())
            {
                udpClient.EnableBroadcast = true; // Включение широковещательного режима
                IPEndPoint remoteEP = new IPEndPoint(IPAddress.Broadcast, broadcastPort);

                byte[] requestBytes = Encoding.UTF8.GetBytes(broadcastRequest);
                await udpClient.SendAsync(requestBytes, requestBytes.Length, remoteEP); // Отправка широковещательного сообщения

                udpClient.Client.ReceiveTimeout = 5000; // Время на получения ответа

                try
                {
                    UdpReceiveResult result = await udpClient.ReceiveAsync(); // Получения ответа
                    string responseData = Encoding.UTF8.GetString(result.Buffer);
                    if (responseData == broadcastResponse) // Проверка ответа
                    {
                        return result.RemoteEndPoint.Address.ToString(); // Получения IP и порта
                    }
                }
                catch (Exception)
                {
                    return null;
                }
            }
            return null;
        }

        private async void OnSendButtonClicked(object sender, RoutedEventArgs e) 
        {
            string message = MessageBox.Text;
            if (!string.IsNullOrWhiteSpace(message))
            {
                try
                {
                    byte[] buffer = Encoding.UTF8.GetBytes(message); 
                    await stream.WriteAsync(buffer, 0, buffer.Length); // Отправка сообщения на сервер
                    MessageBox.Clear();

                    //ChatBox.Text += $"Я: {message}\n";
                }
                catch (Exception ex)
                {
                    ChatBox.Text += $"Ошибка отправки сообщения: {ex.Message}\n";
                }
            }
        }

        private async void OnSendFileButtonClicked(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog 
            {
                Title = "Выберите нужный файл"
            };

            string[] result = await openFileDialog.ShowAsync(this);

            if (result != null && result.Length > 0) // Если выбран файл
            {
                string filePath = result[0]; // Получение пути к выбранному файлу
                try
                {
                    string fileName = Path.GetFileName(filePath); // Получение имени файла
                    byte[] fileNameBytes = Encoding.UTF8.GetBytes($"FILE:{fileName}\0");
                    await stream.WriteAsync(fileNameBytes, 0, fileNameBytes.Length); // Отправка имени файла на сервер
                    await SendFile(filePath); // Отправка файла на сервер
                    ChatBox.Text += $"Файл {Path.GetFileName(filePath)} отправлен.\n";
                }
                catch (Exception ex)
                {
                    ChatBox.Text += $"Ошибка отправки файла: {ex.Message}\n";
                }
            }
        }

        private async Task SendFile(string filePath) // Метод для отправки файла на сервер
        {
            const int BufferSize = 8192;
            byte[] buffer = new byte[BufferSize];

            try
            {
                using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                {
                    int bytesRead;
                    while ((bytesRead = await fileStream.ReadAsync(buffer, 0, buffer.Length)) > 0) // Чтение данных из файла в буфер
                    {
                        await stream.WriteAsync(buffer, 0, bytesRead); // Отправка данных на сервер
                    }
                }

                byte[] endOfFileIndicator = Encoding.UTF8.GetBytes("END_OF_FILE");
                await stream.WriteAsync(endOfFileIndicator, 0, endOfFileIndicator.Length); // Отправка индикатора конца файла на сервер
                await stream.FlushAsync();
            }
            catch (Exception ex)
            {
                ChatBox.Text += $"Ошибка отправки файла: {ex.Message}\n";
            }
        }

        private async Task ReceiveMessages() // Метод для получения сообщений от сервера
        {
            byte[] buffer = new byte[8192];

            try
            {
                int bytesRead;
                while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    string message = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    if (message.StartsWith("FILE:"))
                    {
                        string fileName = message.Substring(5).TrimEnd('\0'); // Получение имени файла
                        await ReceiveFile(fileName); // Получение файла
                    }
                    else if (message.Contains("END_OF_FILE")) 
                    {
                        Dispatcher.UIThread.Post(() => ChatBox.Text += "Отправка файла завершена.\n");
                    }
                    else
                    {
                        Dispatcher.UIThread.Post(() => ChatBox.Text += message + "\n");
                    }
                }
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => ChatBox.Text += $"Ошибка получения сообщения: {ex.Message}\n");
            }
        }

        private async Task ReceiveFile(string fileName) // Метод получения файла от сервера
        {
            const int BufferSize = 8192;
            byte[] buffer = new byte[BufferSize]; 
            string filePath = Path.Combine(fileSavePath, fileName); 

            EnsureDirectoryExists(fileSavePath); // Проверка существования директории 

            try
            {
                using (FileStream fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write))
                {
                    int bytesRead;
                    while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        string checkEndOfFile = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                        if (checkEndOfFile.Contains("END_OF_FILE")) 
                        {
                            byte[] endBuffer = Encoding.UTF8.GetBytes("END_OF_FILE");
                            bytesRead -= endBuffer.Length; 
                            await fileStream.WriteAsync(buffer, 0, bytesRead); 
                            break;
                        }
                        else
                        {
                            await fileStream.WriteAsync(buffer, 0, bytesRead); // Запись данных из буфера в файл
                        }
                    }
                }

                Dispatcher.UIThread.Post(() => ChatBox.Text += $"Файл {fileName} сохранен.\n"); 
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => ChatBox.Text += $"Ошибка сохранения файла: {ex.Message}\n");
            }
        }

        private void EnsureDirectoryExists(string path) // Метод проверки существования директории
        {
            if (!Directory.Exists(path)) // Проверка существования директории
            {
                Directory.CreateDirectory(path); // Создание директории
            }
        }
    }
}
