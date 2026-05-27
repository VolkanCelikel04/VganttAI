import 'dart:convert';

import 'package:http/http.dart' as http;

import 'app_config.dart';

class ApiClient {
  ApiClient({http.Client? httpClient})
    : _httpClient = httpClient ?? http.Client();

  final http.Client _httpClient;

  Future<LoginResult> login({
    required String username,
    required String password,
  }) async {
    if (AppConfig.useMockBackend) {
      await Future<void>.delayed(const Duration(milliseconds: 500));
      return const LoginResult(
        token: 'mock-jwt-token',
        tenantName: 'Demo ERP Tenant',
        userDisplayName: 'ERP Kullanicisi',
      );
    }

    final response = await _post('/auth/login', {
      'username': username,
      'password': password,
    });

    return LoginResult.fromJson(response);
  }

  Future<AssistantAnswer> ask({
    required String token,
    required String question,
    List<AssistantHistoryEntry> history = const [],
  }) async {
    if (AppConfig.useMockBackend) {
      await Future<void>.delayed(const Duration(milliseconds: 700));
      return AssistantAnswer(
        summary: 'Demo veride son faturalar listelendi.',
        sql: '''
select invoice_no, customer_name, total_amount, invoice_date
from sales_invoices
order by invoice_date desc
limit 10''',
        columns: const [
          'invoice_no',
          'customer_name',
          'total_amount',
          'invoice_date',
        ],
        rows: const [
          ['INV-2026-0012', 'Acme A.S.', '18.450,00', '2026-05-21'],
          ['INV-2026-0011', 'Nova Ltd.', '7.920,50', '2026-05-20'],
          ['INV-2026-0010', 'Kuzey Ticaret', '3.180,00', '2026-05-19'],
        ],
      );
    }

    final response = await _post('/assistant/ask', {
      'question': question,
      'history': [for (final item in history) item.toJson()],
    }, token: token);

    return AssistantAnswer.fromJson(response);
  }

  Future<Map<String, dynamic>> _post(
    String path,
    Map<String, dynamic> body, {
    String? token,
  }) async {
    final uri = Uri.parse('${AppConfig.apiBaseUrl}$path');
    final response = await _httpClient.post(
      uri,
      headers: {
        'content-type': 'application/json',
        if (token != null) 'authorization': 'Bearer $token',
      },
      body: jsonEncode(body),
    );

    if (response.statusCode < 200 || response.statusCode >= 300) {
      var message = 'API hata verdi: ${response.statusCode}';
      try {
        final errorJson = jsonDecode(response.body) as Map<String, dynamic>;
        if (errorJson['message'] != null) {
          message = '$message - ${errorJson['message']}';
        }
      } catch (_) {
        // Keep the status-only message if the response is not JSON.
      }

      throw ApiException(message);
    }

    return jsonDecode(response.body) as Map<String, dynamic>;
  }
}

class ApiException implements Exception {
  const ApiException(this.message);

  final String message;

  @override
  String toString() => message;
}

class LoginResult {
  const LoginResult({
    required this.token,
    required this.tenantName,
    required this.userDisplayName,
  });

  factory LoginResult.fromJson(Map<String, dynamic> json) {
    return LoginResult(
      token: json['token'] as String,
      tenantName: json['tenantName'] as String,
      userDisplayName: json['userDisplayName'] as String,
    );
  }

  final String token;
  final String tenantName;
  final String userDisplayName;
}

class AssistantHistoryEntry {
  const AssistantHistoryEntry({
    required this.role,
    required this.text,
    this.sql,
    this.columns = const [],
    this.rows = const [],
  });

  final String role;
  final String text;
  final String? sql;
  final List<String> columns;
  final List<List<String>> rows;

  Map<String, dynamic> toJson() {
    return {
      'role': role,
      'text': text,
      if (sql != null && sql!.trim().isNotEmpty) 'sql': sql,
      if (columns.isNotEmpty) 'columns': columns,
      if (rows.isNotEmpty) 'rows': rows,
    };
  }
}

class AssistantAnswer {
  const AssistantAnswer({
    required this.summary,
    required this.sql,
    required this.columns,
    required this.rows,
  });

  factory AssistantAnswer.fromJson(Map<String, dynamic> json) {
    return AssistantAnswer(
      summary: json['summary'] as String,
      sql: json['sql'] as String,
      columns: (json['columns'] as List<dynamic>).cast<String>(),
      rows: (json['rows'] as List<dynamic>)
          .map(
            (row) => (row as List<dynamic>).map((value) => '$value').toList(),
          )
          .toList(),
    );
  }

  final String summary;
  final String sql;
  final List<String> columns;
  final List<List<String>> rows;
}
