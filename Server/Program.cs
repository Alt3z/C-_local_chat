﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

class ChatServer
{
    static Dictionary<int, TcpClient> clients = new Dictionary<int, TcpClient>();
    static Dictionary<int, string> clientNames = new Dictionary<int, string>();
    static int clientCounter = 0;
    static TcpListener listener;
    static CancellationTokenSource cts = new CancellationTokenSource();

    static void Main(string[] args)
    {
        listener = new TcpListener(IPAddress.Any, 8888);
        listener.Start();  // Запуск слушателя
        Console.WriteLine("Сервер запущен. Ожидание подключений...");

        StartBroadcastListener();
        StartClientHandlerLoop();
        
        Thread.Sleep(Timeout.Infinite);
    }

    static async void StartClientHandlerLoop()// Метод для обработки подключений клиентов
    {
        while (true)
        {
            TcpClient client = await listener.AcceptTcpClientAsync();  // Ожидание нового подключения
            clientCounter++;
            lock (clients)
            {
                clients.Add(clientCounter, client);  // Добавление нового клиента в словарь
            }

            // Создание и запуск нового потока для обработки клиента
            Thread clientThread = new Thread(new ParameterizedThreadStart(HandleClient));
            clientThread.Start(clientCounter);
        }
    }

    static async void StartBroadcastListener() // Метод для прослушивания широковещательных сообщений
    {
        UdpClient udpListener = new UdpClient(8888);
        udpListener.EnableBroadcast = true;
        //IPEndPoint remoteEP = new IPEndPoint(IPAddress.Any, 8888);

        while (true)
        {
            UdpReceiveResult receivedResults = await udpListener.ReceiveAsync();  // Ожидание получения сообщения
            string receivedData = Encoding.UTF8.GetString(receivedResults.Buffer); 
            if (receivedData == "DISCOVER_CHAT_SERVER") 
            {
                byte[] responseData = Encoding.UTF8.GetBytes("CHAT_SERVER_RESPONSE");
                await udpListener.SendAsync(responseData, responseData.Length, receivedResults.RemoteEndPoint);
            }
        }
    }

    static void HandleClient(object clientIdObj) // Метод обработки сообщений от клиента
    {
        int clientId = (int)clientIdObj;
        TcpClient client = clients[clientId];  // Получение клиента из словаря
        NetworkStream stream = client.GetStream();
        byte[] buffer = new byte[8192];  // Буфер для чтения данных
        int bytesRead;

        try
        {
            // Чтение имени пользователя от клиента
            bytesRead = stream.Read(buffer, 0, buffer.Length);
            string userName = Encoding.UTF8.GetString(buffer, 0, bytesRead).TrimEnd('\0');  
            clientNames.Add(clientId, userName);

            Console.WriteLine($"{userName} подключился к чату.");
            BroadcastMessage(clientId, $"{userName} присоединился к чату.");  // Рассылка сообщения о новом подключении всем клиентам

            while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0) // Цикл для чтения сообщений от клиента
            {
                string message = Encoding.UTF8.GetString(buffer, 0, bytesRead).TrimEnd('\0');

                if (message.StartsWith("FILE:")) 
                {
                    string fileName = message.Substring(5).TrimEnd('\0');  // Извлечение имени файла
                    Console.WriteLine($"{userName} начал передачу файла: {fileName}");
                    HandleFileTransfer(clientId, fileName, stream);  // Обработка передачи файла
                }
                else
                {
                    Console.WriteLine($"{userName}: {message}"); 
                    BroadcastMessage(clientId, $"{userName}: {message}");
                }
            }
        }
        catch (Exception e)
        {
            Console.WriteLine("Ошибка: " + e.Message);
        }
        finally // Если клиент вышел
        {
            if (clientNames.ContainsKey(clientId))
            {
                string userName = clientNames[clientId]; 
                client.Close();  // Закрытие подключения
                lock (clients) 
                {
                    clients.Remove(clientId);  // Удаление клиента из словаря
                }
                clientNames.Remove(clientId);  // Удаление имени клиента из словаря
                BroadcastMessage(clientId, $"{userName} покинул чат.");
                Console.WriteLine($"{userName} отключился.");
            }
        }
    }

    static void BroadcastMessage(int senderId, string message)// Метод  рассылки сообщения всем клиентам
    {
        byte[] buffer = Encoding.UTF8.GetBytes(message);
        List<TcpClient> clientsCopy;
        lock (clients)
        {
            clientsCopy = new List<TcpClient>(clients.Values); 
        }

        foreach (var client in clientsCopy)// Цикл для отправки сообщения каждому клиенту
        {
            if (!client.Connected)  // Если подключен клиент
            {
                continue;
            }

            try
            {
                NetworkStream stream = client.GetStream();
                stream.Write(buffer, 0, buffer.Length);  // Отправка сообщения
                stream.Flush();  // Закрытие потока
            }
            catch (Exception e)
            {
                Console.WriteLine("Ошибка: " + e.Message);
            }
        }
    }

    static void HandleFileTransfer(int senderId, string fileName, NetworkStream senderStream) // Метод для обработки передачи файлов от клиента
    {
        byte[] buffer = new byte[8192];
        int bytesRead;
        string filePath = Path.Combine("ReceivedFiles", fileName);  // Определение пути для сохранения файла

        Directory.CreateDirectory("ReceivedFiles");  // Создание директории для файлов, если она не существует

        try
        {
            using (var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write))
            {
                while ((bytesRead = senderStream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    string checkEndOfFile = Encoding.UTF8.GetString(buffer, 0, bytesRead);  // Проверка конца файла
                    if (checkEndOfFile.Contains("END_OF_FILE"))
                    {
                        byte[] endBuffer = Encoding.UTF8.GetBytes("END_OF_FILE");  // Удаление индикатора конца файла из байтов
                        bytesRead -= endBuffer.Length;
                        fileStream.Write(buffer, 0, bytesRead);  // Запись данных в файл
                        break;
                    }
                    else
                    {
                        fileStream.Write(buffer, 0, bytesRead);  // Запись данных в файл
                    }
                }
            }

            lock (clients)
            {
                foreach (var client in clients)
                {
                    if (client.Key != senderId)  // Проверка, не является ли клиент отправителем файла
                    {
                        NetworkStream receiverStream = client.Value.GetStream();
                        byte[] fileNameBytes = Encoding.UTF8.GetBytes($"FILE:{fileName}\0"); 
                        receiverStream.Write(fileNameBytes, 0, fileNameBytes.Length);  // Отправка имени файла
                        receiverStream.Flush();  // Закрытие потока

                        // Отправка файла клиенту
                        using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                        {
                            while ((bytesRead = fileStream.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                receiverStream.Write(buffer, 0, bytesRead);
                            }
                        }

                        byte[] endOfFileIndicator = Encoding.UTF8.GetBytes("END_OF_FILE"); 
                        receiverStream.Write(endOfFileIndicator, 0, endOfFileIndicator.Length);
                        receiverStream.Flush();
                    }
                }
            }

            Console.WriteLine($"Передача файла {fileName} завершена.");
        }
        catch (Exception e)
        {
            Console.WriteLine("Ошибка при передаче файла: " + e.Message);
        }
    }
}