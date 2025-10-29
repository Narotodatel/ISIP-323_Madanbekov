using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;

namespace AutoServiceGame
{
    public class Part
    {
        public int PartID { get; set; }
        public string PartName { get; set; }
        public decimal PurchasePrice { get; set; }
        public decimal WorkPrice { get; set; }
        public decimal PenaltyPrice { get; set; }
        public int Quantity { get; set; }
        public decimal TotalPrice => PurchasePrice + WorkPrice;
    }

    public class CustomerOrder
    {
        public int OrderID { get; set; }
        public string CustomerName { get; set; }
        public Part BrokenPart { get; set; }
        public DateTime OrderDate { get; set; }
        public string Status { get; set; }
        public Part UsedPart { get; set; }
        public decimal Revenue { get; set; }
    }

    public class PurchaseOrder
    {
        public int OrderID { get; set; }
        public Part Part { get; set; }
        public int Quantity { get; set; }
        public DateTime OrderDate { get; set; }
        public int ArrivalCounter { get; set; }
    }

    public class GameManager
    {
        private string _connectionString;
        private Random _random;

        public decimal CurrentBalance { get; private set; }
        public int CustomerCounter { get; private set; }
        public bool IsGameActive { get; private set; }

        public GameManager(string connectionString)
        {
            _connectionString = connectionString;
            _random = new Random();
            LoadGameState();
        }

        private void LoadGameState()
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                connection.Open();
                var command = new SqlCommand("SELECT CurrentBalance, CustomerCounter, IsGameActive FROM GameState", connection);
                using (var reader = command.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        CurrentBalance = (decimal)reader["CurrentBalance"];
                        CustomerCounter = (int)reader["CustomerCounter"];
                        IsGameActive = (bool)reader["IsGameActive"];
                    }
                }
            }
        }

        private void UpdateGameState()
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                connection.Open();
                var command = new SqlCommand(
                    "UPDATE GameState SET CurrentBalance = @Balance, CustomerCounter = @Counter, LastUpdate = GETDATE()",
                    connection);
                command.Parameters.AddWithValue("@Balance", CurrentBalance);
                command.Parameters.AddWithValue("@Counter", CustomerCounter);
                command.ExecuteNonQuery();
            }
        }

        private void AddFinanceRecord(string operationType, decimal amount, string description = null)
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                connection.Open();
                var command = new SqlCommand(
                    "INSERT INTO Finance (OperationType, Amount, BalanceAfterOperation, Description) " +
                    "VALUES (@Type, @Amount, @Balance, @Description)",
                    connection);
                command.Parameters.AddWithValue("@Type", operationType);
                command.Parameters.AddWithValue("@Amount", amount);
                command.Parameters.AddWithValue("@Balance", CurrentBalance);
                command.Parameters.AddWithValue("@Description", description ?? (object)DBNull.Value);
                command.ExecuteNonQuery();
            }
        }

        public List<Part> GetAvailableParts()
        {
            var parts = new List<Part>();
            using (var connection = new SqlConnection(_connectionString))
            {
                connection.Open();
                var command = new SqlCommand(
                    "SELECT p.PartID, p.PartName, p.PurchasePrice, p.WorkPrice, p.PenaltyPrice, " +
                    "COALESCE(w.Quantity, 0) as Quantity " +
                    "FROM Parts p LEFT JOIN Warehouse w ON p.PartID = w.PartID",
                    connection);

                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        parts.Add(new Part
                        {
                            PartID = (int)reader["PartID"],
                            PartName = (string)reader["PartName"],
                            PurchasePrice = (decimal)reader["PurchasePrice"],
                            WorkPrice = (decimal)reader["WorkPrice"],
                            PenaltyPrice = (decimal)reader["PenaltyPrice"],
                            Quantity = (int)reader["Quantity"]
                        });
                    }
                }
            }
            return parts;
        }

        public CustomerOrder GenerateNewCustomer()
        {
            var availableParts = GetAvailableParts();
            var brokenPart = availableParts[_random.Next(availableParts.Count)];

            var customerNames = new[] { "Иван", "Мария", "Петр", "Анна", "Сергей", "Ольга", "Дмитрий", "Елена" };
            var customerName = customerNames[_random.Next(customerNames.Length)];

            return new CustomerOrder
            {
                CustomerName = customerName,
                BrokenPart = brokenPart,
                OrderDate = DateTime.Now,
                Status = "Pending"
            };
        }

        public bool AcceptOrder(CustomerOrder order)
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                connection.Open();
                var transaction = connection.BeginTransaction();

                try
                {
                    var checkCommand = new SqlCommand(
                        "SELECT Quantity FROM Warehouse WHERE PartID = @PartID",
                        connection, transaction);
                    checkCommand.Parameters.AddWithValue("@PartID", order.BrokenPart.PartID);

                    var quantity = (int?)checkCommand.ExecuteScalar();

                    if (quantity.HasValue && quantity.Value > 0)
                    {
                        var updateCommand = new SqlCommand(
                            "UPDATE Warehouse SET Quantity = Quantity - 1 WHERE PartID = @PartID",
                            connection, transaction);
                        updateCommand.Parameters.AddWithValue("@PartID", order.BrokenPart.PartID);
                        updateCommand.ExecuteNonQuery();
                        CurrentBalance += order.BrokenPart.TotalPrice;
                        AddFinanceRecord("Ремонт", order.BrokenPart.TotalPrice,
                            $"Успешный ремонт для {order.CustomerName}");
                        SaveOrder(order, "Completed", order.BrokenPart.PartID, order.BrokenPart.TotalPrice);

                        CustomerCounter++;
                        UpdateGameState();

                        transaction.Commit();
                        return true;
                    }
                    else
                    {
                        var availableParts = GetPartsInStock(connection, transaction);
                        if (availableParts.Any())
                        {
                            var randomPart = availableParts[_random.Next(availableParts.Count)];
                            var updateCommand = new SqlCommand(
                                "UPDATE Warehouse SET Quantity = Quantity - 1 WHERE PartID = @PartID",
                                connection, transaction);
                            updateCommand.Parameters.AddWithValue("@PartID", randomPart.PartID);
                            updateCommand.ExecuteNonQuery();
                            var penalty = order.BrokenPart.PenaltyPrice * 2;
                            CurrentBalance -= penalty;
                            AddFinanceRecord("Штраф", -penalty,
                                $"Неправильный ремонт для {order.CustomerName}");

                            SaveOrder(order, "Failed", randomPart.PartID, -penalty);

                            CustomerCounter++;
                            UpdateGameState();

                            transaction.Commit();
                            return false;
                        }
                        else
                        {
                            var penalty = order.BrokenPart.PenaltyPrice;
                            CurrentBalance -= penalty;
                            AddFinanceRecord("Штраф за отказ", -penalty,
                                $"Отказ в обслуживании {order.CustomerName}");

                            SaveOrder(order, "Refused", null, -penalty);

                            CustomerCounter++;
                            UpdateGameState();

                            transaction.Commit();
                            return false;
                        }
                    }
                }
                catch
                {
                    transaction.Rollback();
                    throw;
                }
            }
        }

        private List<Part> GetPartsInStock(SqlConnection connection, SqlTransaction transaction)
        {
            var parts = new List<Part>();
            var command = new SqlCommand(
                "SELECT p.PartID, p.PartName FROM Parts p " +
                "INNER JOIN Warehouse w ON p.PartID = w.PartID " +
                "WHERE w.Quantity > 0",
                connection, transaction);

            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    parts.Add(new Part
                    {
                        PartID = (int)reader["PartID"],
                        PartName = (string)reader["PartName"]
                    });
                }
            }
            return parts;
        }

        private void SaveOrder(CustomerOrder order, string status, int? usedPartId, decimal revenue)
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                connection.Open();
                var command = new SqlCommand(
                    "INSERT INTO CustomerOrders (CustomerName, BrokenPartID, Status, UsedPartID, Revenue) " +
                    "VALUES (@Customer, @BrokenPart, @Status, @UsedPart, @Revenue)",
                    connection);

                command.Parameters.AddWithValue("@Customer", order.CustomerName);
                command.Parameters.AddWithValue("@BrokenPart", order.BrokenPart.PartID);
                command.Parameters.AddWithValue("@Status", status);
                command.Parameters.AddWithValue("@UsedPart", usedPartId ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("@Revenue", revenue);

                command.ExecuteNonQuery();
            }
        }

        public void PurchaseParts(int partId, int quantity)
        {
            var part = GetAvailableParts().FirstOrDefault(p => p.PartID == partId);
            if (part == null) throw new ArgumentException("Деталь не найдена");

            var totalCost = part.PurchasePrice * quantity;
            if (CurrentBalance < totalCost)
                throw new InvalidOperationException("Недостаточно средств");

            using (var connection = new SqlConnection(_connectionString))
            {
                connection.Open();
                var command = new SqlCommand(
                    "INSERT INTO PurchaseOrders (PartID, Quantity, ArrivalCounter) " +
                    "VALUES (@PartID, @Quantity, 2)",
                    connection);

                command.Parameters.AddWithValue("@PartID", partId);
                command.Parameters.AddWithValue("@Quantity", quantity);
                command.ExecuteNonQuery();
            }

            CurrentBalance -= totalCost;
            AddFinanceRecord("Покупка деталей", -totalCost,
                $"Заказ {quantity} шт. {part.PartName}");

            UpdateGameState();
        }

        public void ProcessArrivingOrders()
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                connection.Open();
                var transaction = connection.BeginTransaction();

                try
                {
                    var updateCommand = new SqlCommand(
                        "UPDATE PurchaseOrders SET ArrivalCounter = ArrivalCounter - 1 " +
                        "WHERE ArrivalCounter > 0",
                        connection, transaction);
                    updateCommand.ExecuteNonQuery();

                    var selectCommand = new SqlCommand(
                        "SELECT PartID, Quantity FROM PurchaseOrders WHERE ArrivalCounter = 0",
                        connection, transaction);

                    using (var reader = selectCommand.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var partId = (int)reader["PartID"];
                            var quantity = (int)reader["Quantity"];

                            var warehouseCommand = new SqlCommand(
                                "UPDATE Warehouse SET Quantity = Quantity + @Quantity WHERE PartID = @PartID; " +
                                "IF @@ROWCOUNT = 0 " +
                                "INSERT INTO Warehouse (PartID, Quantity) VALUES (@PartID, @Quantity)",
                                connection, transaction);

                            warehouseCommand.Parameters.AddWithValue("@PartID", partId);
                            warehouseCommand.Parameters.AddWithValue("@Quantity", quantity);
                            warehouseCommand.ExecuteNonQuery();
                        }
                    }

                    var deleteCommand = new SqlCommand(
                        "DELETE FROM PurchaseOrders WHERE ArrivalCounter = 0",
                        connection, transaction);
                    deleteCommand.ExecuteNonQuery();

                    transaction.Commit();
                }
                catch
                {
                    transaction.Rollback();
                    throw;
                }
            }
        }

        public bool CheckGameOver()
        {
            if (CurrentBalance <= 0)
            {
                var parts = GetAvailableParts();
                var totalParts = parts.Sum(p => p.Quantity);

                if (totalParts == 0)
                {
                    IsGameActive = false;
                    using (var connection = new SqlConnection(_connectionString))
                    {
                        connection.Open();
                        var command = new SqlCommand(
                            "UPDATE GameState SET IsGameActive = 0", connection);
                        command.ExecuteNonQuery();
                    }
                    return true;
                }
            }
            return false;
        }
    }

    class Program
    {
        static void Main(string[] args)
        {
            var connectionString = "Your_Connection_String_Here";
            var game = new GameManager(connectionString);

            Console.WriteLine("Добро пожаловать в автосервис!");
            Console.WriteLine($"Текущий баланс: {game.CurrentBalance:C}");

            while (game.IsGameActive)
            {
                game.ProcessArrivingOrders();

                var customer = game.GenerateNewCustomer();
                Console.WriteLine($"\nПриехал клиент: {customer.CustomerName}");
                Console.WriteLine($"Сломанная деталь: {customer.BrokenPart.PartName}");
                Console.WriteLine($"Стоимость ремонта: {customer.BrokenPart.TotalPrice:C}");
                Console.WriteLine($"Штраф за отказ: {customer.BrokenPart.PenaltyPrice:C}");

                Console.WriteLine("\n1 - Принять заказ");
                Console.WriteLine("2 - Отказать");
                Console.WriteLine("3 - Купить детали");
                Console.WriteLine("4 - Показать склад");
                Console.WriteLine("5 - Выход");

                var choice = Console.ReadLine();

                switch (choice)
                {
                    case "1":
                        var success = game.AcceptOrder(customer);
                        if (success)
                            Console.WriteLine("Ремонт выполнен успешно!");
                        else
                            Console.WriteLine("Проблема с ремонтом!");
                        break;
                    case "2":
                        break;
                    case "3":
                        ShowPurchaseMenu(game);
                        break;
                    case "4":
                        ShowWarehouse(game);
                        break;
                    case "5":
                        return;
                }

                if (game.CheckGameOver())
                {
                    Console.WriteLine("\nИгра окончена! Вы банкрот.");
                    break;
                }

                Console.WriteLine($"\nТекущий баланс: {game.CurrentBalance:C}");
            }
        }

        static void ShowPurchaseMenu(GameManager game)
        {
            var parts = game.GetAvailableParts();
            Console.WriteLine("\nДоступные детали для покупки:");

            for (int i = 0; i < parts.Count; i++)
            {
                Console.WriteLine($"{i + 1}. {parts[i].PartName} - {parts[i].PurchasePrice:C} (на складе: {parts[i].Quantity})");
            }

            Console.Write("Выберите деталь: ");
            if (int.TryParse(Console.ReadLine(), out int partIndex) && partIndex > 0 && partIndex <= parts.Count)
            {
                Console.Write("Количество: ");
                if (int.TryParse(Console.ReadLine(), out int quantity) && quantity > 0)
                {
                    try
                    {
                        game.PurchaseParts(parts[partIndex - 1].PartID, quantity);
                        Console.WriteLine("Заказ размещен! Детали прибудут через 2 клиента.");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Ошибка: {ex.Message}");
                    }
                }
            }
        }

        static void ShowWarehouse(GameManager game)
        {
            var parts = game.GetAvailableParts();
            Console.WriteLine("\nТекущее состояние склада:");
            foreach (var part in parts)
            {
                Console.WriteLine($"{part.PartName}: {part.Quantity} шт.");
            }
        }
    }
}