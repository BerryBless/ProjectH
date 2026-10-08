using ProjectH.Monitoring;

// Monitoring D7. Ctrl+C / SIGTERM stops Kestrel, the OfflineSweeper and disposes the host (request §55).
MonitoringApp.Create(args).Run();
