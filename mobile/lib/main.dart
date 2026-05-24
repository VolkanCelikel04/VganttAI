import 'package:flutter/material.dart';
import 'package:flutter_tts/flutter_tts.dart';
import 'package:speech_to_text/speech_recognition_result.dart';
import 'package:speech_to_text/speech_to_text.dart' as stt;

import 'core/api_client.dart';

void main() {
  runApp(const VganttAiAssistantApp());
}

class VganttAiAssistantApp extends StatelessWidget {
  const VganttAiAssistantApp({super.key});

  @override
  Widget build(BuildContext context) {
    final colorScheme = ColorScheme.fromSeed(
      seedColor: const Color(0xFF16697A),
      brightness: Brightness.light,
    );

    return MaterialApp(
      debugShowCheckedModeBanner: false,
      title: 'Vgantt ERP AI',
      theme: ThemeData(
        colorScheme: colorScheme,
        scaffoldBackgroundColor: const Color(0xFFF7F8FA),
        useMaterial3: true,
        inputDecorationTheme: const InputDecorationTheme(
          border: OutlineInputBorder(),
        ),
      ),
      home: LoginScreen(apiClient: ApiClient()),
    );
  }
}

class Session {
  const Session({
    required this.token,
    required this.tenantName,
    required this.userDisplayName,
  });

  final String token;
  final String tenantName;
  final String userDisplayName;
}

class LoginScreen extends StatefulWidget {
  const LoginScreen({required this.apiClient, super.key});

  final ApiClient apiClient;

  @override
  State<LoginScreen> createState() => _LoginScreenState();
}

class _LoginScreenState extends State<LoginScreen> {
  final _formKey = GlobalKey<FormState>();
  final _usernameController = TextEditingController(
    text: 'volkan.celikel@vgantt.com',
  );
  final _passwordController = TextEditingController();
  var _isLoading = false;
  String? _error;

  @override
  void dispose() {
    _usernameController.dispose();
    _passwordController.dispose();
    super.dispose();
  }

  Future<void> _login() async {
    if (!_formKey.currentState!.validate()) {
      return;
    }

    setState(() {
      _isLoading = true;
      _error = null;
    });

    try {
      final result = await widget.apiClient.login(
        username: _usernameController.text.trim(),
        password: _passwordController.text,
      );

      if (!mounted) {
        return;
      }

      Navigator.of(context).pushReplacement(
        MaterialPageRoute<void>(
          builder: (_) => AssistantScreen(
            apiClient: widget.apiClient,
            session: Session(
              token: result.token,
              tenantName: result.tenantName,
              userDisplayName: result.userDisplayName,
            ),
          ),
        ),
      );
    } catch (error) {
      setState(() {
        _error = '$error';
      });
    } finally {
      if (mounted) {
        setState(() {
          _isLoading = false;
        });
      }
    }
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      body: SafeArea(
        child: Center(
          child: SingleChildScrollView(
            padding: const EdgeInsets.all(24),
            child: ConstrainedBox(
              constraints: const BoxConstraints(maxWidth: 460),
              child: Form(
                key: _formKey,
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    Icon(
                      Icons.analytics_outlined,
                      size: 56,
                      color: Theme.of(context).colorScheme.primary,
                    ),
                    const SizedBox(height: 20),
                    Text(
                      'Vgantt ERP AI',
                      textAlign: TextAlign.center,
                      style: Theme.of(context).textTheme.headlineMedium
                          ?.copyWith(fontWeight: FontWeight.w700),
                    ),
                    const SizedBox(height: 8),
                    Text(
                      'Musteri ERP verisini guvenli SELECT sorgulariyla analiz edin.',
                      textAlign: TextAlign.center,
                      style: Theme.of(context).textTheme.bodyMedium,
                    ),
                    const SizedBox(height: 32),
                    TextFormField(
                      controller: _usernameController,
                      textInputAction: TextInputAction.next,
                      decoration: const InputDecoration(
                        labelText: 'Kullanici adi',
                        prefixIcon: Icon(Icons.person_outline),
                      ),
                      validator: (value) =>
                          value == null || value.trim().isEmpty
                          ? 'Kullanici adi gerekli'
                          : null,
                    ),
                    const SizedBox(height: 14),
                    TextFormField(
                      controller: _passwordController,
                      obscureText: true,
                      decoration: const InputDecoration(
                        labelText: 'Sifre',
                        prefixIcon: Icon(Icons.lock_outline),
                      ),
                      onFieldSubmitted: (_) => _login(),
                      validator: (value) => value == null || value.isEmpty
                          ? 'Sifre gerekli'
                          : null,
                    ),
                    if (_error != null) ...[
                      const SizedBox(height: 14),
                      Text(
                        _error!,
                        style: TextStyle(
                          color: Theme.of(context).colorScheme.error,
                        ),
                      ),
                    ],
                    const SizedBox(height: 22),
                    FilledButton.icon(
                      onPressed: _isLoading ? null : _login,
                      icon: _isLoading
                          ? const SizedBox.square(
                              dimension: 18,
                              child: CircularProgressIndicator(strokeWidth: 2),
                            )
                          : const Icon(Icons.login),
                      label: const Text('Giris yap'),
                    ),
                  ],
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }
}

class AssistantScreen extends StatefulWidget {
  const AssistantScreen({
    required this.apiClient,
    required this.session,
    super.key,
  });

  final ApiClient apiClient;
  final Session session;

  @override
  State<AssistantScreen> createState() => _AssistantScreenState();
}

class _AssistantScreenState extends State<AssistantScreen> {
  final _questionController = TextEditingController(
    text: 'Bugun acilan satis siparisleri ne kadar?',
  );
  final _speechToText = stt.SpeechToText();
  final _flutterTts = FlutterTts();
  final List<_ChatMessage> _messages = const [
    _ChatMessage(
      role: _MessageRole.assistant,
      text:
          'Merhaba. ERP veriniz icin sadece okuma amacli sorular sorabilirsiniz.',
    ),
  ].toList();
  AssistantAnswer? _latestAnswer;
  var _isLoading = false;
  var _speechReady = false;
  var _isListening = false;
  var _isSpeaking = false;
  String? _voiceStatus;

  @override
  void initState() {
    super.initState();
    _configureSpeech();
    _configureTts();
  }

  @override
  void dispose() {
    _speechToText.cancel();
    _flutterTts.stop();
    _questionController.dispose();
    super.dispose();
  }

  Future<void> _configureSpeech() async {
    final ready = await _speechToText.initialize(
      onStatus: (status) {
        if (!mounted) {
          return;
        }

        setState(() {
          _isListening = status == 'listening';
          if (status == 'done' || status == 'notListening') {
            _voiceStatus = null;
          }
        });
      },
      onError: (error) {
        if (!mounted) {
          return;
        }

        setState(() {
          _isListening = false;
          _voiceStatus = error.errorMsg;
        });
      },
    );

    if (!mounted) {
      return;
    }

    setState(() {
      _speechReady = ready;
      _voiceStatus = ready ? null : 'Ses tanima kullanilamiyor';
    });
  }

  Future<void> _configureTts() async {
    await _flutterTts.setLanguage('tr-TR');
    await _flutterTts.setSpeechRate(0.48);
    await _flutterTts.setPitch(1);
    await _flutterTts.setVolume(1);
    await _flutterTts.awaitSpeakCompletion(false);
    _flutterTts.setStartHandler(() {
      if (mounted) {
        setState(() => _isSpeaking = true);
      }
    });
    _flutterTts.setCompletionHandler(() {
      if (mounted) {
        setState(() => _isSpeaking = false);
      }
    });
    _flutterTts.setCancelHandler(() {
      if (mounted) {
        setState(() => _isSpeaking = false);
      }
    });
    _flutterTts.setErrorHandler((_) {
      if (mounted) {
        setState(() => _isSpeaking = false);
      }
    });
  }

  Future<void> _toggleListening() async {
    if (_isLoading) {
      return;
    }

    await _flutterTts.stop();

    if (!_speechReady) {
      await _configureSpeech();
      if (!_speechReady) {
        return;
      }
    }

    if (_speechToText.isListening) {
      await _speechToText.stop();
      return;
    }

    setState(() {
      _voiceStatus = 'Dinleniyor';
    });

    await _speechToText.listen(
      onResult: _onSpeechResult,
      listenOptions: stt.SpeechListenOptions(
        localeId: 'tr_TR',
        listenMode: stt.ListenMode.dictation,
        pauseFor: const Duration(seconds: 2),
        listenFor: const Duration(seconds: 20),
      ),
    );
  }

  void _onSpeechResult(SpeechRecognitionResult result) {
    final words = result.recognizedWords.trim();
    if (words.isEmpty) {
      return;
    }

    setState(() {
      _questionController.text = words;
      _questionController.selection = TextSelection.collapsed(
        offset: _questionController.text.length,
      );
    });

    if (result.finalResult) {
      _ask();
    }
  }

  Future<void> _ask() async {
    final question = _questionController.text.trim();
    if (question.isEmpty || _isLoading) {
      return;
    }

    setState(() {
      _messages.add(_ChatMessage(role: _MessageRole.user, text: question));
      _isLoading = true;
      _latestAnswer = null;
      _voiceStatus = null;
      _questionController.clear();
    });

    try {
      final answer = await widget.apiClient.ask(
        token: widget.session.token,
        question: question,
      );

      setState(() {
        _latestAnswer = answer;
        _messages.add(
          _ChatMessage(role: _MessageRole.assistant, text: answer.summary),
        );
      });
      await _speakAnswer(answer);
    } catch (error) {
      setState(() {
        _messages.add(
          _ChatMessage(role: _MessageRole.assistant, text: '$error'),
        );
      });
    } finally {
      if (mounted) {
        setState(() {
          _isLoading = false;
        });
      }
    }
  }

  Future<void> _speakAnswer(AssistantAnswer answer) async {
    final spokenAnswer = _buildSpokenAnswer(answer);
    if (spokenAnswer.isEmpty) {
      return;
    }

    await _flutterTts.stop();
    await _flutterTts.speak(spokenAnswer);
  }

  String _buildSpokenAnswer(AssistantAnswer answer) {
    if (answer.rows.isEmpty) {
      return answer.summary;
    }

    final rowCountText = '${answer.rows.length} satir sonuc geldi.';
    final firstRow = answer.rows.first;
    final details = <String>[];

    for (var index = 0; index < answer.columns.length; index += 1) {
      if (index >= firstRow.length) {
        break;
      }

      details.add('${answer.columns[index]}: ${firstRow[index]}');
    }

    if (details.isEmpty) {
      return '${answer.summary} $rowCountText';
    }

    return '${answer.summary} $rowCountText Ilk sonuc: ${details.join(', ')}.';
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(
        title: Text(widget.session.tenantName),
        actions: [
          IconButton(
            tooltip: 'Oturumu kapat',
            onPressed: () {
              Navigator.of(context).pushReplacement(
                MaterialPageRoute<void>(
                  builder: (_) => LoginScreen(apiClient: widget.apiClient),
                ),
              );
            },
            icon: const Icon(Icons.logout),
          ),
        ],
      ),
      body: SafeArea(
        child: Column(
          children: [
            _TenantHeader(session: widget.session),
            Expanded(
              child: ListView.separated(
                padding: const EdgeInsets.all(16),
                itemBuilder: (context, index) =>
                    _MessageBubble(message: _messages[index]),
                separatorBuilder: (_, _) => const SizedBox(height: 10),
                itemCount: _messages.length,
              ),
            ),
            if (_latestAnswer != null) _ResultPreview(answer: _latestAnswer!),
            Padding(
              padding: const EdgeInsets.fromLTRB(16, 10, 16, 16),
              child: Column(
                mainAxisSize: MainAxisSize.min,
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  if (_voiceStatus != null) ...[
                    Text(
                      _voiceStatus!,
                      style: Theme.of(context).textTheme.bodySmall?.copyWith(
                        color: Theme.of(context).colorScheme.primary,
                      ),
                    ),
                    const SizedBox(height: 8),
                  ],
                  Row(
                    children: [
                      Expanded(
                        child: TextField(
                          controller: _questionController,
                          minLines: 1,
                          maxLines: 4,
                          decoration: const InputDecoration(
                            hintText:
                                'Orn: Bugun acilan satis siparisleri ne kadar?',
                          ),
                          onSubmitted: (_) => _ask(),
                        ),
                      ),
                      const SizedBox(width: 8),
                      IconButton.filledTonal(
                        tooltip: _isListening ? 'Dinlemeyi durdur' : 'Konus',
                        onPressed: _isLoading ? null : _toggleListening,
                        icon: Icon(_isListening ? Icons.mic : Icons.mic_none),
                      ),
                      const SizedBox(width: 8),
                      IconButton.filled(
                        tooltip: 'Sor',
                        onPressed: _isLoading ? null : _ask,
                        icon: _isLoading
                            ? const SizedBox.square(
                                dimension: 18,
                                child: CircularProgressIndicator(
                                  strokeWidth: 2,
                                ),
                              )
                            : const Icon(Icons.send),
                      ),
                      if (_isSpeaking) ...[
                        const SizedBox(width: 8),
                        IconButton.outlined(
                          tooltip: 'Sesi durdur',
                          onPressed: _flutterTts.stop,
                          icon: const Icon(Icons.volume_off_outlined),
                        ),
                      ],
                    ],
                  ),
                ],
              ),
            ),
          ],
        ),
      ),
    );
  }
}

class _TenantHeader extends StatelessWidget {
  const _TenantHeader({required this.session});

  final Session session;

  @override
  Widget build(BuildContext context) {
    return Container(
      width: double.infinity,
      padding: const EdgeInsets.fromLTRB(16, 10, 16, 12),
      color: Theme.of(context).colorScheme.surface,
      child: Row(
        children: [
          const Icon(Icons.verified_user_outlined),
          const SizedBox(width: 10),
          Expanded(
            child: Text(
              '${session.userDisplayName} aktif. Tenant baglantisi backend tarafinda cozulecek.',
              style: Theme.of(context).textTheme.bodySmall,
            ),
          ),
        ],
      ),
    );
  }
}

class _ResultPreview extends StatelessWidget {
  const _ResultPreview({required this.answer});

  final AssistantAnswer answer;

  @override
  Widget build(BuildContext context) {
    return Container(
      constraints: const BoxConstraints(maxHeight: 230),
      margin: const EdgeInsets.symmetric(horizontal: 16),
      decoration: BoxDecoration(
        color: Theme.of(context).colorScheme.surface,
        border: Border.all(color: Theme.of(context).dividerColor),
        borderRadius: BorderRadius.circular(8),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Padding(
            padding: const EdgeInsets.all(12),
            child: Text(
              answer.sql,
              style: Theme.of(
                context,
              ).textTheme.bodySmall?.copyWith(fontFamily: 'monospace'),
            ),
          ),
          const Divider(height: 1),
          Expanded(
            child: SingleChildScrollView(
              scrollDirection: Axis.horizontal,
              child: SingleChildScrollView(
                child: DataTable(
                  columns: [
                    for (final column in answer.columns)
                      DataColumn(label: Text(column)),
                  ],
                  rows: [
                    for (final row in answer.rows)
                      DataRow(
                        cells: [for (final value in row) DataCell(Text(value))],
                      ),
                  ],
                ),
              ),
            ),
          ),
        ],
      ),
    );
  }
}

class _MessageBubble extends StatelessWidget {
  const _MessageBubble({required this.message});

  final _ChatMessage message;

  @override
  Widget build(BuildContext context) {
    final isUser = message.role == _MessageRole.user;
    final colorScheme = Theme.of(context).colorScheme;

    return Align(
      alignment: isUser ? Alignment.centerRight : Alignment.centerLeft,
      child: Container(
        constraints: const BoxConstraints(maxWidth: 340),
        padding: const EdgeInsets.all(12),
        decoration: BoxDecoration(
          color: isUser ? colorScheme.primary : colorScheme.surface,
          borderRadius: BorderRadius.circular(8),
          border: isUser
              ? null
              : Border.all(color: Theme.of(context).dividerColor),
        ),
        child: Text(
          message.text,
          style: TextStyle(
            color: isUser ? colorScheme.onPrimary : colorScheme.onSurface,
          ),
        ),
      ),
    );
  }
}

class _ChatMessage {
  const _ChatMessage({required this.role, required this.text});

  final _MessageRole role;
  final String text;
}

enum _MessageRole { user, assistant }
