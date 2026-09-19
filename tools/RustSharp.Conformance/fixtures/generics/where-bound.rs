trait Mark {}
impl Mark for bool {}
fn keep<T>(value: T) -> T where T: Mark { value }
fn main() { keep(true); }
