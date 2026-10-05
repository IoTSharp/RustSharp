// frozen P1 fixture: byref-import-call
// The package reference is checked by the platform runner with --require;
// the executable MIR body exercises the same shared-borrow shape locally
// until external MIR calls have a first-class lowering path.
use ByrefProducer::read as imported_read;

fn local_read(value: &i32) -> i32 {
    *value
}

fn main() {
    let value = 7;
    println!("{}", local_read(&value));
}
