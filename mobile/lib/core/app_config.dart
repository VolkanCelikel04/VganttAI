class AppConfig {
  const AppConfig._();

  static const apiBaseUrl = String.fromEnvironment(
    'API_BASE_URL',
    defaultValue: 'https://api.vgantt.com',
  );

  static const useMockBackend = bool.fromEnvironment(
    'MOCK_BACKEND',
    defaultValue: false,
  );
}
