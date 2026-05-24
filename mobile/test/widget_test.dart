import 'package:flutter_test/flutter_test.dart';

import 'package:vgantt_ai_assistant/main.dart';

void main() {
  testWidgets('shows login screen', (WidgetTester tester) async {
    await tester.pumpWidget(const VganttAiAssistantApp());

    expect(find.text('Vgantt ERP AI'), findsOneWidget);
    expect(find.text('Giris yap'), findsOneWidget);
  });
}
