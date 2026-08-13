import { ChangeDetectionStrategy, Component } from '@angular/core';

import { ShellComponent } from './layout/shell/shell.component';

// Run-time dynamic layout loading is replaced by a static, compile-time shell. None of that survives.

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [ShellComponent],
  templateUrl: './app.component.html',
  styleUrl: './app.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  // This metadata deliberately registers NOTHING with the injector, and the absence is load-bearing. Every
  // dependency the application needs is declared exactly once, in `appConfig` in `app.config.ts`, which
  // `src/main.ts` hands whole to `bootstrapApplication`.
})
export class AppComponent {}
