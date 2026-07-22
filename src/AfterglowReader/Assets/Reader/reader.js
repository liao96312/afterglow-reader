(() => {
  const content = document.getElementById('content');
  const chapters = document.getElementById('chapters');
  const openFile = document.getElementById('open-file');
  const directoryToggle = document.getElementById('directory-toggle');
  const autoScrollButton = document.getElementById('auto-scroll');
  const settingsToggle = document.getElementById('settings-toggle');
  const settingsPanel = document.getElementById('settings-panel');
  const fontFamily = document.getElementById('font-family');
  const fontSize = document.getElementById('font-size');
  const fontWeight = document.getElementById('font-weight');
  const textColor = document.getElementById('text-color');
  const lineHeight = document.getElementById('line-height');
  const letterSpacing = document.getElementById('letter-spacing');
  const opacity = document.getElementById('opacity');
  const opaquePage = document.getElementById('opaque-page');
  const scrollSpeed = document.getElementById('scroll-speed');
  const settingsConfirm = document.getElementById('settings-confirm');
  const settingsCancel = document.getElementById('settings-cancel');
  const appLabel = document.getElementById('app-label');
  const toast = document.getElementById('toast');
  const toolbar = document.getElementById('toolbar');
  const completion = document.getElementById('book-complete');
  const completionOpen = document.getElementById('complete-open');
  const completionDirectory = document.getElementById('complete-directory');
  const completionRestart = document.getElementById('complete-restart');
  let raf = 0;
  let targetScroll = 0;
  let autoScroll = false;
  let autoSpeed = 40;
  let lastFrame = 0;
  let windowRequestPending = false;
  let windowRequestDirection = 0;
  let windowRequestCooldownDirection = 0;
  let progressTimer = 0;
  let progressSequence = 0;
  let dragState = null;
  let toastTimer = 0;
  let toolbarHideTimer = 0;
  let toolbarRevealTimer = 0;
  let readerInteractionRequested = false;
  let hasBook = false;
  let completionArmed = false;
  let currentBookKey = '';
  let lastScrollY = 0;
  let committedSettings = null;
  let settingsSnapshot = null;
  let settingsAnchor = null;

  const showToast = (message) => {
    if (!toast) return;
    window.clearTimeout(toastTimer);
    toast.textContent = message;
    toast.hidden = false;
    toastTimer = window.setTimeout(() => { toast.hidden = true; }, 3000);
  };

  const showToolbar = () => {
    window.clearTimeout(toolbarRevealTimer);
    document.body.classList.remove('toolbar-hidden');
    window.clearTimeout(toolbarHideTimer);
  };

  const hideToolbar = () => {
    if (!settingsPanel.classList.contains('hidden')) cancelSettings(false);
    document.body.classList.add('toolbar-hidden');
    chapters.classList.add('hidden');
    settingsPanel.classList.add('hidden');
    if (document.activeElement && document.activeElement !== document.body) {
      content.tabIndex = -1;
      content.focus({ preventScroll: true });
    }
    window.clearTimeout(toolbarHideTimer);
  };

  const scheduleToolbarHide = () => {
    window.clearTimeout(toolbarHideTimer);
    toolbarHideTimer = window.setTimeout(hideToolbar, 2000);
  };

  const setAutoScrollState = (active) => {
    autoScroll = active;
    autoScrollButton.classList.toggle('active', autoScroll);
    autoScrollButton.textContent = autoScroll ? '暂停滚动' : '自动滚动';
  };

  toolbar.addEventListener('pointerenter', showToolbar);
  toolbar.addEventListener('pointerleave', scheduleToolbarHide);
  settingsPanel.addEventListener('pointerenter', showToolbar);
  settingsPanel.addEventListener('pointerleave', scheduleToolbarHide);
  chapters.addEventListener('pointerenter', showToolbar);
  chapters.addEventListener('pointerleave', scheduleToolbarHide);
  addEventListener('click', (event) => {
    if (!document.body.classList.contains('toolbar-hidden') || event.clientY > 64 || event.target.closest('.window-resize-handle')) return;
    window.clearTimeout(toolbarRevealTimer);
    toolbarRevealTimer = window.setTimeout(() => {
      showToolbar();
      scheduleToolbarHide();
    }, 400);
  }, true);

  openFile.onclick = () => {
    showToolbar();
    completion.classList.add('hidden');
    showToast('正在打开文件…');
    window.chrome?.webview?.postMessage(JSON.stringify({ type: 'openFile' }));
  };
  completionOpen.onclick = () => openFile.click();
  completionDirectory.onclick = () => {
    chapters.classList.remove('hidden');
    showToolbar();
  };
  completionRestart.onclick = () => {
    completion.classList.add('hidden');
    completionArmed = false;
    scrollTo(0, 0);
    targetScroll = 0;
    showToolbar();
  };
  directoryToggle.onclick = () => {
    chapters.classList.toggle('hidden');
    showToolbar();
  };
  const readSettingsControls = () => ({
    fontFamily: fontFamily.value,
    fontSize: Number(fontSize.value),
    fontWeight: fontWeight.value,
    textColor: textColor.value,
    lineHeight: Number(lineHeight.value),
    letterSpacing: Number(letterSpacing.value),
    opacity: Number(opacity.value),
    opaquePage: opaquePage.checked,
    scrollPixelsPerSecond: Number(scrollSpeed.value)
  });
  const applySettingsToPage = (settings) => {
    document.documentElement.style.setProperty('--font-family', settings.fontFamily || 'Microsoft YaHei');
    document.documentElement.style.setProperty('--font-size', `${settings.fontSize || 20}px`);
    document.documentElement.style.setProperty('--font-weight', settings.fontWeight || '400');
    document.documentElement.style.setProperty('--text-color', settings.textColor || '#2a2521');
    document.documentElement.style.setProperty('--line-height', settings.lineHeight || 1.9);
    document.documentElement.style.setProperty('--letter-spacing', `${Number(settings.letterSpacing) || 0}px`);
    document.documentElement.style.setProperty('--chrome-alpha', settings.opacity || 0.94);
    document.documentElement.style.setProperty('--reader-background', settings.opaquePage ? `rgb(247 243 234 / ${settings.opacity || 0.94})` : 'transparent');
    autoSpeed = settings.scrollPixelsPerSecond || 220;
  };
  const fillSettingsControls = (settings) => {
    fontFamily.value = settings.fontFamily || 'Microsoft YaHei';
    fontSize.value = settings.fontSize || 20;
    fontWeight.value = settings.fontWeight || '400';
    textColor.value = settings.textColor || '#2a2521';
    lineHeight.value = settings.lineHeight || 1.9;
    letterSpacing.value = Number(settings.letterSpacing) || 0;
    opacity.value = settings.opacity || 0.94;
    opaquePage.checked = Boolean(settings.opaquePage);
    scrollSpeed.value = settings.scrollPixelsPerSecond || 220;
  };
  const beginSettings = () => {
    if (!settingsSnapshot) {
      settingsSnapshot = { ...(committedSettings || readSettingsControls()) };
      settingsAnchor = window.captureCurrentAnchor?.() || null;
    }
    settingsPanel.classList.remove('hidden');
    showToolbar();
  };
  const previewSettings = () => {
    beginSettings();
    applySettingsToPage(readSettingsControls());
    if (settingsAnchor?.id) requestAnimationFrame(() => restoreAnchor(settingsAnchor.id, settingsAnchor.offset));
  };
  const publishSettings = () => {
    const settings = readSettingsControls();
    committedSettings = { ...settings };
    window.chrome?.webview?.postMessage(JSON.stringify({ type: 'settingsChanged', ...settings }));
  };
  const cancelSettings = (close = true) => {
    if (settingsSnapshot) {
      fillSettingsControls(settingsSnapshot);
      applySettingsToPage(settingsSnapshot);
      if (settingsAnchor?.id) requestAnimationFrame(() => restoreAnchor(settingsAnchor.id, settingsAnchor.offset));
    }
    settingsSnapshot = null;
    settingsAnchor = null;
    if (close) settingsPanel.classList.add('hidden');
  };
  window.openSettings = beginSettings;
  settingsToggle.onclick = () => settingsPanel.classList.contains('hidden') ? beginSettings() : cancelSettings();
  [fontFamily, fontSize, fontWeight, textColor, lineHeight, letterSpacing, opacity, opaquePage, scrollSpeed].forEach(control => control.addEventListener('input', previewSettings));
  settingsConfirm.onclick = () => {
    publishSettings();
    settingsSnapshot = null;
    settingsAnchor = null;
    settingsPanel.classList.add('hidden');
    scheduleToolbarHide();
  };
  settingsCancel.onclick = () => { cancelSettings(); scheduleToolbarHide(); };
  window.setFontFamilies = (families) => {
    const selected = fontFamily.value;
    fontFamily.replaceChildren();
    for (const name of families) {
      const option = document.createElement('option');
      option.value = name;
      option.textContent = name;
      option.style.fontFamily = name;
      fontFamily.append(option);
    }
    fontFamily.value = selected;
  };
  window.applyReaderSettings = (settings) => {
    committedSettings = { ...settings };
    fillSettingsControls(settings);
    applySettingsToPage(settings);
  };
  autoScrollButton.onclick = () => {
    if (!autoScroll && scrollY >= Math.max(0, document.documentElement.scrollHeight - innerHeight - 2)) {
      showToast('已经到达书末');
      setAutoScrollState(false);
      return;
    }
    window.toggleAutoScroll?.();
    showToolbar();
  };

  const postWindowAction = (message) => (event) => {
    if (event.button !== 0) return;
    event.preventDefault();
    event.stopPropagation();
    window.chrome?.webview?.postMessage(JSON.stringify(message));
  };

  appLabel.addEventListener('pointerdown', postWindowAction({ type: 'beginWindowDrag' }));
  for (const handle of document.querySelectorAll('.window-resize-handle')) {
    handle.addEventListener('pointerdown', postWindowAction({
      type: 'beginWindowResize',
      edge: handle.dataset.edge
    }));
  }

  const requestReaderInteraction = () => {
    if (readerInteractionRequested) return;
    readerInteractionRequested = true;
    window.chrome?.webview?.postMessage(JSON.stringify({ type: 'readerPointerEntered' }));
  };

  document.addEventListener('pointerenter', requestReaderInteraction, true);
  addEventListener('pointermove', requestReaderInteraction, { passive: true });
  window.resetReaderInteraction = () => { readerInteractionRequested = false; };

  window.setChapterIndex = (items) => {
    chapters.replaceChildren();
    for (const item of items) {
      const link = document.createElement('button');
      link.type = 'button';
      link.dataset.chapterId = item.id;
      link.textContent = item.title;
      link.onclick = () => window.chrome?.webview?.postMessage(JSON.stringify({ type: 'selectChapter', chapterId: item.id }));
      chapters.append(link);
    }
  };

  const setActiveChapter = (id) => {
    for (const link of chapters.querySelectorAll('button')) {
      link.classList.toggle('active', link.dataset.chapterId === id);
    }
  };

  const restoreAnchor = (anchorId, anchorOffset) => {
    if (!anchorId) return false;
    const anchor = document.querySelector(`[data-paragraph-id="${CSS.escape(anchorId)}"]`);
    if (!anchor) return false;
    const targetOffset = Number.isFinite(anchorOffset) ? anchorOffset : 0;
    anchor.scrollIntoView({ block: 'start' });
    const correct = () => {
      if (!anchor.isConnected) return;
      const delta = anchor.getBoundingClientRect().top - targetOffset;
      if (Math.abs(delta) > 0.5) scrollBy(0, delta);
      targetScroll = scrollY;
    };
    correct();
    requestAnimationFrame(correct);
    requestAnimationFrame(() => requestAnimationFrame(correct));
    return true;
  };

  window.renderWindow = (payload) => {
    const book = payload.book;
    const bookKey = book.Path || book.Title || '';
    const restoringSameBook = currentBookKey !== '' && currentBookKey === bookKey && Boolean(payload.anchorId);
    lastScrollY = scrollY;
    completionArmed = restoringSameBook;
    completion.classList.add('hidden');
    currentBookKey = bookKey;
    setAutoScrollState(false);
    content.replaceChildren();
    hasBook = true;
    const title = document.createElement('h1');
    title.textContent = book.Title;
    content.append(title);
    for (const chapter of book.Chapters) {
      const section = document.createElement('section');
      section.id = chapter.Id;
      if (chapter.Title.trim() !== chapter.Paragraphs[0]?.PlainText?.trim()) {
        const heading = document.createElement('h2');
        heading.textContent = chapter.Title;
        section.append(heading);
      }
      for (const paragraph of chapter.Paragraphs) {
        const node = document.createElement('p');
        node.dataset.paragraphId = paragraph.Id;
        node.innerHTML = paragraph.Html;
        section.append(node);
      }
      content.append(section);
    }
    showToast(`${book.Title} · ${book.Chapters.length} 章`);
    setActiveChapter(payload.selectedChapterId || book.Chapters[0]?.Id);
    if (!restoreAnchor(payload.anchorId, payload.anchorOffset)) {
      scrollTo(0, 0);
      targetScroll = scrollY;
    }
    requestAnimationFrame(() => { lastScrollY = scrollY; });
    const completedDirection = windowRequestDirection;
    windowRequestDirection = 0;
    windowRequestPending = false;
    windowRequestCooldownDirection = completedDirection;
  };

  window.windowRequestHandled = (hasMore = true) => {
    const completedDirection = windowRequestDirection;
    windowRequestDirection = 0;
    windowRequestPending = false;
    windowRequestCooldownDirection = completedDirection;
    if (!hasMore) {
      windowRequestCooldownDirection = 0;
      setAutoScrollState(false);
      targetScroll = scrollY;
    }
  };

  const anchorForWindowRequest = () => {
    const nodes = [...document.querySelectorAll('[data-paragraph-id]')];
    const visible = nodes.find(node => node.getBoundingClientRect().bottom > 0);
    const anchor = visible || nodes[0];
    return anchor ? { id: anchor.dataset.paragraphId, offset: anchor.getBoundingClientRect().top } : { id: null, offset: 0 };
  };

  window.captureCurrentAnchor = () => ({ ...anchorForWindowRequest(), sequence: progressSequence });

  const requestWindow = (direction) => {
    if (windowRequestPending || !window.chrome?.webview) return;
    windowRequestPending = true;
    windowRequestDirection = Math.sign(direction);
    const anchor = anchorForWindowRequest();
    window.chrome.webview.postMessage(JSON.stringify({ type: 'requestWindow', direction, anchorId: anchor.id, anchorOffset: anchor.offset }));
  };

  const publishProgress = () => {
    progressTimer = 0;
    if (!window.chrome?.webview) return;
    const anchor = anchorForWindowRequest();
    window.chrome.webview.postMessage(JSON.stringify({ type: 'progressChanged', paragraphId: anchor.id, offset: anchor.offset, sequence: ++progressSequence }));
  };

  const updateActiveFromScroll = () => {
    const section = [...content.querySelectorAll('section')].find(node => node.getBoundingClientRect().bottom > 0);
    if (section) setActiveChapter(section.id);
  };

  const updateCompletion = () => {
    if (!hasBook || !completionArmed) {
      completion.classList.add('hidden');
      return;
    }
    const bottom = document.documentElement.scrollHeight - innerHeight;
    completion.classList.toggle('hidden', scrollY < Math.max(0, bottom - 2));
  };

  const clampTarget = (value) => Math.max(0, Math.min(value, document.documentElement.scrollHeight - innerHeight));
  const animate = (now) => {
    const dt = Math.min(0.05, Math.max(0.001, (now - lastFrame) / 1000));
    lastFrame = now;
    if (autoScroll) targetScroll = clampTarget(targetScroll + autoSpeed * dt);
    const distance = targetScroll - scrollY;
    const maxStep = (autoScroll ? autoSpeed : 1600) * dt;
    const step = Math.sign(distance) * Math.min(Math.abs(distance) * Math.min(1, dt * 12), maxStep);
    if (Math.abs(step) > 0.1) scrollBy(0, step);
    if (autoScroll && scrollY >= document.documentElement.scrollHeight - innerHeight - 1) {
      setAutoScrollState(false);
      targetScroll = scrollY;
    }
    if (autoScroll || Math.abs(targetScroll - scrollY) > 0.5) raf = requestAnimationFrame(animate);
    else raf = 0;
  };

  const startAnimation = () => {
    if (!raf) {
      lastFrame = performance.now();
      raf = requestAnimationFrame(animate);
    }
  };

  window.toggleAutoScroll = () => {
    setAutoScrollState(!autoScroll);
    targetScroll = scrollY;
    if (autoScroll) startAnimation();
  };

  addEventListener('wheel', (event) => {
    if (event.ctrlKey) return;
    const unit = event.deltaMode === 1 ? 16 : event.deltaMode === 2 ? innerHeight : 1;
    const delta = event.deltaY * unit;
    if (event.deltaMode === 0 && Math.abs(delta) < 40) return;
    event.preventDefault();
    targetScroll = clampTarget(targetScroll + delta);
    startAnimation();
  }, { passive: false });

  const isInteractiveTarget = (target) => target instanceof Element
    && Boolean(target.closest('a, button, input, textarea, select, [contenteditable="true"], [data-window-action]'));

  addEventListener('pointerdown', (event) => {
    if (event.button !== 0 || isInteractiveTarget(event.target)) return;
    dragState = {
      pointerId: event.pointerId,
      startX: event.clientX,
      startY: event.clientY,
      startScroll: scrollY,
      active: false
    };
  });

  addEventListener('pointermove', (event) => {
    if (!dragState || dragState.pointerId !== event.pointerId) return;
    const dx = event.clientX - dragState.startX;
    const dy = event.clientY - dragState.startY;
    if (!dragState.active && Math.hypot(dx, dy) < 6) return;
    dragState.active = true;
    setAutoScrollState(false);
    document.documentElement.classList.add('dragging');
    targetScroll = clampTarget(dragState.startScroll - dy);
    startAnimation();
    event.preventDefault();
  }, { passive: false });

  const finishDrag = (event) => {
    if (!dragState || dragState.pointerId !== event.pointerId) return;
    if (dragState.active) event.preventDefault();
    document.documentElement.classList.remove('dragging');
    dragState = null;
  };

  addEventListener('pointerup', finishDrag);
  addEventListener('pointercancel', finishDrag);

  addEventListener('keydown', (event) => {
    if (event.target && ['INPUT', 'TEXTAREA', 'SELECT'].includes(event.target.tagName)) return;
    if (!autoScroll) {
      if (raf && ['ArrowUp', 'ArrowDown', 'PageUp', 'PageDown'].includes(event.key)) {
        cancelAnimationFrame(raf);
        raf = 0;
        targetScroll = scrollY;
      }
      return;
    }
    if (event.key === 'ArrowUp') { autoSpeed = Math.max(10, autoSpeed - 10); event.preventDefault(); }
    if (event.key === 'ArrowDown') { autoSpeed = Math.min(400, autoSpeed + 10); event.preventDefault(); }
    if (event.key === ' ') { setAutoScrollState(false); targetScroll = scrollY; event.preventDefault(); }
  });

  addEventListener('scroll', () => {
    if (!raf && !autoScroll && !dragState) targetScroll = scrollY;
    updateActiveFromScroll();
    if (scrollY > lastScrollY + 1) completionArmed = true;
    lastScrollY = scrollY;
    updateCompletion();
    if (!progressTimer) progressTimer = setTimeout(publishProgress, 250);
  }, { passive: true });

  showToolbar();
  scheduleToolbarHide();
  window.chrome?.webview?.postMessage(JSON.stringify({ type: 'readerReady' }));
})();
