using Xunit;

// Живые сборки пишут один кэш баз и один движок: параллельные прогоны отбирают друг у друга
// временный пак и дают плавающие падения. Тесты идут по очереди.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
