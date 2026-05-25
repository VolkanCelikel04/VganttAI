class AppConfig {
  const AppConfig._();

  static const apiBaseUrl = String.fromEnvironment(
    'API_BASE_URL',
    defaultValue: 'http://192.168.1.180:5055',
  );

  static const useMockBackend = bool.fromEnvironment(
    'MOCK_BACKEND',
    defaultValue: false,
  );
}
