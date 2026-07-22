# Architecture

WPF/.NET 8. `ThreeMfService` isole la lecture ZIP/XML sécurisée et l’export ; `PaletteService` produit les palettes ; `MainWindow` orchestre l’interface et le visualiseur WPF 3D. Les opérations de fichier sont asynchrones côté interface.
