struct Flag(bool);

impl containers::Allowed for Flag {}

fn main() {
    let number = containers::make(42);
    println!("{}", containers::unwrap(number));
    let flag = containers::make(Flag(true));
    println!("{}", containers::unwrap(flag).0);
}
