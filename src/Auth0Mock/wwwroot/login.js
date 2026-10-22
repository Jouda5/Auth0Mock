(() => {
  const form = document.querySelector('#login-form');
  const email = document.querySelector('#email');
  const suggestions = document.querySelector('#recent-emails');
  const clearButton = document.querySelector('#clear-emails');
  const status = document.querySelector('#email-history-status');
  const continueButton = document.querySelector('#continue-button');
  const continueLabel = document.querySelector('#continue-label');
  const continueArrow = document.querySelector('#continue-arrow');
  const continueSpinner = document.querySelector('#continue-spinner');
  const submissionError = document.querySelector('#submission-error');
  const restartLogin = document.querySelector('#restart-login');
  const storageKey = 'auth0mock.recent-emails.v1';
  const maxSavedEmails = 8;

  if (!form || !email || !suggestions || !clearButton || !status
    || !continueButton || !continueLabel || !continueArrow || !continueSpinner
    || !submissionError || !restartLogin) {
    console.error('Auth0Mock login form is missing expected elements.');
    return;
  }

  const loadEmails = () => {
    const saved = localStorage.getItem(storageKey);
    if (saved === null) {
      return [];
    }

    const values = JSON.parse(saved);
    if (!Array.isArray(values)) {
      throw new Error('Saved login email list has an invalid format.');
    }

    return values
      .filter(value => typeof value === 'string' && value.length <= 254 && value.includes('@'))
      .slice(0, maxSavedEmails);
  };

  const renderEmails = values => {
    suggestions.replaceChildren(...values.map(value => {
      const option = document.createElement('option');
      option.value = value;
      return option;
    }));
    clearButton.hidden = values.length === 0;
  };

  let recentEmails;
  try {
    recentEmails = loadEmails();
    renderEmails(recentEmails);
  } catch (error) {
    console.warn('Could not load saved sign-in addresses from this browser.', error);
    recentEmails = [];
    status.textContent = 'Saved addresses are unavailable in this browser.';
  }

  const rememberEmail = value => {
    const normalized = value.trim().toLowerCase();
    recentEmails = [normalized, ...recentEmails.filter(item => item !== normalized)].slice(0, maxSavedEmails);
    try {
      localStorage.setItem(storageKey, JSON.stringify(recentEmails));
      renderEmails(recentEmails);
      status.textContent = 'Recent addresses are saved on this browser only.';
    } catch (error) {
      console.warn('Could not save this sign-in address in this browser.', error);
      status.textContent = 'This browser could not save the address.';
    }
  };

  let submitting = false;
  let submissionTimeout;
  restartLogin.href = window.location.href;

  const resetSubmission = () => {
    clearTimeout(submissionTimeout);
    submitting = false;
    continueButton.disabled = false;
    continueButton.removeAttribute('aria-busy');
    continueLabel.textContent = 'Continue as this user';
    continueArrow.hidden = false;
    continueSpinner.hidden = true;
    submissionError.hidden = true;
    restartLogin.hidden = true;
  };

  const showSubmissionError = message => {
    clearTimeout(submissionTimeout);
    continueButton.removeAttribute('aria-busy');
    continueLabel.textContent = 'Sign-in did not finish';
    continueSpinner.hidden = true;
    submissionError.textContent = message;
    submissionError.hidden = false;
    restartLogin.hidden = false;
  };

  window.addEventListener('pageshow', resetSubmission);
  document.addEventListener('securitypolicyviolation', event => {
    if (submitting && event.effectiveDirective === 'form-action') {
      showSubmissionError('The browser blocked the sign-in redirect. Start a fresh sign-in. If this repeats, check the configured callback URLs.');
    }
  });

  email.addEventListener('keydown', event => {
    if (event.key === 'Enter' && !event.isComposing) {
      event.preventDefault();
      if (!submitting) {
        form.requestSubmit(continueButton);
      }
    }
  });

  clearButton.addEventListener('click', () => {
    try {
      localStorage.removeItem(storageKey);
      recentEmails = [];
      renderEmails(recentEmails);
      status.textContent = 'Saved addresses cleared from this browser.';
    } catch (error) {
      console.warn('Could not clear saved sign-in addresses from this browser.', error);
      status.textContent = 'This browser could not clear saved addresses.';
    }
  });

  form.addEventListener('submit', event => {
    if (submitting) {
      event.preventDefault();
      return;
    }
    submitting = true;
    rememberEmail(email.value);
    continueButton.disabled = true;
    continueButton.setAttribute('aria-busy', 'true');
    continueLabel.textContent = 'Continuing...';
    continueArrow.hidden = true;
    continueSpinner.hidden = false;
    submissionTimeout = setTimeout(() => {
      showSubmissionError('No redirect was received within 10 seconds. Start a fresh sign-in rather than resubmitting this request.');
    }, 10000);
  });
})();
