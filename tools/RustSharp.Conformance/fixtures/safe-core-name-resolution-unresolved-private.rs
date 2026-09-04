mod hidden {
    fn secret() {}
}

fn caller() {
    crate::hidden::secret();
    missing();
}
