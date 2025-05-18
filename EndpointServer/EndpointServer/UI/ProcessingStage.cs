using System;
using System.Collections.Generic;
using Terminal.Gui;

namespace ParallelComputingEndpoint
{
    /// <summary>
    /// Rappresenta lo stato corrente del processo di elaborazione del nodo
    /// </summary>
    public enum ProcessingStage
    {
        Idle,
        ReceivingData,
        ProcessingData,
        SendingResults,
        Completed,
        Failed
    }

    /// <summary>
    /// Classe che tiene traccia del progresso dell'elaborazione del nodo e lo visualizza nell'interfaccia utente
    /// </summary>
    public class NodeProcessingProgressTracker
    {
        private Dialog _progressDialog;
        private Label _stageLabel;
        private Label _detailsLabel;
        private ProgressBar _progressBar;
        private Label _percentLabel;
        private Button _cancelButton;
        private bool _isCancelled = false;
        private DateTime _startTime;
        private ProcessingStage _currentStage = ProcessingStage.Idle;
        private int _currentPercent = 0;
        private LogPanel _logPanel;

        public event EventHandler CancellationRequested;

        /// <summary>
        /// Costruttore che prende un riferimento al LogPanel per aggiungere messaggi
        /// </summary>
        public NodeProcessingProgressTracker(LogPanel logPanel)
        {
            _logPanel = logPanel;
        }

        /// <summary>
        /// Mostra il dialogo della barra di progresso
        /// </summary>
        public void Show(string nodeType)
        {
            _startTime = DateTime.Now;
            _isCancelled = false;

            Application.MainLoop.Invoke(() => {
                // Crea un nuovo dialogo modale
                _progressDialog = new Dialog($"Elaborazione {nodeType}", 60, 12);

                // Etichetta per lo stadio corrente
                _stageLabel = new Label("Inizializzazione...")
                {
                    X = 1,
                    Y = 1,
                    Width = Dim.Fill() - 2
                };
                _progressDialog.Add(_stageLabel);

                // Etichetta per i dettagli
                _detailsLabel = new Label("")
                {
                    X = 1,
                    Y = 3,
                    Width = Dim.Fill() - 2
                };
                _progressDialog.Add(_detailsLabel);

                // Barra di progresso
                _progressBar = new ProgressBar()
                {
                    X = 1,
                    Y = 5,
                    Width = Dim.Fill() - 10,
                    Height = 1,
                    Fraction = 0
                };
                _progressDialog.Add(_progressBar);

                // Etichetta percentuale
                _percentLabel = new Label("0%")
                {
                    X = Pos.Right(_progressBar) + 1,
                    Y = 5,
                    Width = 8
                };
                _progressDialog.Add(_percentLabel);

                // Bottone Annulla
                _cancelButton = new Button("Annulla")
                {
                    X = Pos.Center(),
                    Y = 7
                };
                _cancelButton.Clicked += () =>
                {
                    _isCancelled = true;
                    CancellationRequested?.Invoke(this, EventArgs.Empty);
                    SetDetails("Annullamento in corso...");
                };
                _progressDialog.Add(_cancelButton);

                // Esegui il dialogo (non bloccante)
                Application.Run(_progressDialog);
            });
        }

        /// <summary>
        /// Imposta lo stadio attuale dell'elaborazione
        /// </summary>
        public void SetStage(ProcessingStage stage, string customMessage = null)
        {
            _currentStage = stage;
            string message = customMessage ?? GetDefaultStageMessage(stage);

            Application.MainLoop.Invoke(() => {
                if (_stageLabel != null)
                {
                    _stageLabel.Text = message;
                    _stageLabel.SetNeedsDisplay();
                }
            });

            _logPanel?.AddLog(message);
        }

        /// <summary>
        /// Imposta il messaggio di dettaglio
        /// </summary>
        public void SetDetails(string details)
        {
            Application.MainLoop.Invoke(() => {
                if (_detailsLabel != null)
                {
                    _detailsLabel.Text = details;
                    _detailsLabel.SetNeedsDisplay();
                }
            });
        }

        /// <summary>
        /// Aggiorna la percentuale di avanzamento
        /// </summary>
        public void UpdateProgress(int percent)
        {
            _currentPercent = percent;
            float fraction = Math.Max(0, Math.Min(100, percent)) / 100f;

            Application.MainLoop.Invoke(() => {
                if (_progressBar != null && _percentLabel != null)
                {
                    _progressBar.Fraction = fraction;
                    _progressBar.SetNeedsDisplay();

                    _percentLabel.Text = $"{percent}%";
                    _percentLabel.SetNeedsDisplay();
                }
            });
        }

        /// <summary>
        /// Chiude il dialogo del progresso
        /// </summary>
        public void Close()
        {
            TimeSpan elapsed = DateTime.Now - _startTime;
            string elapsedTime = $"{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";

            _logPanel?.AddLog($"Operazione completata in {elapsedTime}. Progresso: {_currentStage}, {_currentPercent}%");

            Application.MainLoop.Invoke(() => {
                if (_progressDialog != null)
                {
                    Application.RequestStop();
                    _progressDialog = null;
                }
            });
        }

        /// <summary>
        /// Verifica se l'operazione è stata annullata
        /// </summary>
        public bool IsCancelled => _isCancelled;

        private string GetDefaultStageMessage(ProcessingStage stage)
        {
            return stage switch
            {
                ProcessingStage.Idle => "In attesa...",
                ProcessingStage.ReceivingData => "Ricezione dati in corso...",
                ProcessingStage.ProcessingData => "Elaborazione dati in corso...",
                ProcessingStage.SendingResults => "Invio risultati in corso...",
                ProcessingStage.Completed => "Elaborazione completata",
                ProcessingStage.Failed => "Errore durante l'elaborazione",
                _ => "Stato sconosciuto"
            };
        }
    }
}